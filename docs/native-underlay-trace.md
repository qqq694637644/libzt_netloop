# logPR #4：libzt Native Underlay 取证日志

本 PR **以 PR #3 的 `gpt/long-term-reliability` 为 base**，只补充可复现切网时序的诊断。
不改变 Binder 刷新/随机端口算法、ZeroTier 的 Peer 发现、Root/Planet 心跳、
SOCKS 连接上限、750ms/3s 协商超时，不创建守护服务，也**不发送额外探测包**。

## 当前证据与调查目标

2026-10-12 的 A/B 双探针：A 直连互联网（10810）持续成功，
但 A 的 NetLoop（1080）经 B 出口在切网后约一分钟不可用。
A 新副 UDP 端口被采样到后，B 约 40 秒才发现该端点。
B 未发生 `max_connections` 拒绝，已有 Relay 则有成批 Native read 错误。
因此需定位：物理刷新提交 → Binder 完成重绑 → UDP 实际出入 → Peer 路径恢复，
并将约 226 秒的 Node 状态周期同上游 Planet 路径区分。

## 实现与边界

Native `0006-native-underlay-trace.patch` 添加只读
`zts_netloop_poll_underlay_event` P/Invoke ABI。NodeService 在最多 **512 条**
的固定上限 FIFO 中记录原生时间戳；C# 每 250ms 最多消费 128 条，
写入 JSONL `libzt_native_underlay_trace`。
丢弃时记录 `libzt_native_underlay_records_dropped`，不阻塞 TCP 业务以等待日志。

| kind | 捕获点 | 关键字段及含义 |
| --- | --- | --- |
| `network_refresh_queued` | Native 收到网络变化请求 | 请求时间，即使被合并亦各自记录 |
| `binder_refresh_started` | Binder.refresh 之前 | `binder_start_native_ms`、`reason_flags`、此前主/副端口 |
| `binder_refresh_completed` | Binder.refresh 返回并公布新快照 | 同一 `binder_start_native_ms`、新主/副端口、`previous_secondary_port`、`byte_count` 为 Binder 地址数、`result` 为主端口是否绑定 |
| `peer_path_checked` | ZeroTier core 候选物理路径的可用性过滤回调 | `peer_id`、`endpoint`、`result=1` 允许 / `0` 拒绝 |
| `wire_udp_sent` | ZeroTier Node wire callback 发生实际 UDP send 之后 | `peer_id` 为报文头中的目的 ID、`endpoint`、`packet_id`、`result=0/-1` 为向 socket 提交状态 |
| `wire_udp_received` | UDP receive 进入 ZeroTier core 后 | `peer_id` 为报文头中的来源 ID、`endpoint`、`packet_id`、`result` 为 core 的 packet processing 结果 |
| `node_phase` | 原生 Node ONLINE/OFFLINE 事件产生时 | `reason_flags=1/0`、`result=201/202`、`last_global_receive_native_ms` |
| `planet_path_snapshot` | 刷新时或处于 OFFLINE 每约 15 秒 | 每个已知 Planet Root 的 Node ID、path_count（`reason_flags`）、最新物理地址、最近 `lastReceive`、`result` 为 Node online 布尔值 |

`binder_refresh_*` 的 `reason_flags` 为位组合：
`1=network_changed`、`2=sleep/wake`、`4=periodic`、
`8=secondary_port_changed`（completed 才有）、`16=node_offline`。
原生刷新“收到”不代表 Peer 就绪；`result=1` 也只证明 Binder **主端口在本地地址集合中**，
不等于外部 NAT 可达。

**协议安全边界：** UDP 事件从 ZeroTier 报文*未验证的固定头*提取
40-bit Node ID 与 64-bit Packet ID。报文内容不记录、不解密、不保存；
无法仅由这些日志证明一个加密包就是 HELLO、OK 或某个 Peer 的认证响应。
被丢弃/分片/过短的包也可能无法被采样。
成功的 `wire_udp_sent` 只证明本地 send API 接受，**不证明 B 收到**；
`wire_udp_received` 只证明 native socket 看到了报文，core 返回码并非
“HELLO 握手成功”。Peer Wire 日志对相同 Node ID + 方向最多每秒一条，
但物理端点变化、发送错误可触发额外记录；高流量未被逐包完整取证。
不同端 Packet ID **恰好都被采样**时可比较，不保证每次 TX 都有 RX 配对。

Planet 快照只提供现有 core Peer 路径信息，**不能直接解释 core 为什么判断 OFFLINE**。
端口也是本机实际 UDP Binder 地址，而不是路由器公网 NAT 映射。
因此本 PR 是调查工具，不把“226 秒周期”视为已修复。

## 人工观测（A + B）

1. A/B 都使用由本 PR 完整构建的相同版本 Native+Managed 二进制；
   保留原有 `state/` 和 identity 文件，不覆盖。
2. 同步 A/B 系统时钟，启用 `--log-file` 的 JSONL 轮转；
   A 同时运行原有独立直连互联网（10810）和 NetLoop 出口（1080）探针。
3. 仅切换一次 A 的 Wi-Fi/热点，记录 UTC 操作时刻，
   不在恢复期间不断反复切换；收集 A/B 的
   `libzt_native_underlay_trace`、`libzt_peer_status`、
   `tcp_accept_completed` 与独立探针日志。
4. 排序时**优先使用 `data.native_timestamp_utc`**；
   `ts` 是托管消费队列的时间，可能晚于 Native 事件。
5. 观察 A 有没有向 B 的新 UDP endpoint 发送报文、B 有没有从
   同一 endpoint 收到、是否只有 Planet 上游无 RX，
   再决定需要修补的是 Binder、Peer 调度还是外部 UDP/NAT。

快速查看：

```powershell
Get-Content .\logs\netloop.jsonl |
  ForEach-Object { $_ | ConvertFrom-Json } |
  Where-Object { $_.event -eq 'libzt_native_underlay_trace' } |
  Select-Object -ExpandProperty data |
  Select-Object native_timestamp_utc,kind,peer_id,endpoint,packet_id,reason_flags,result,secondary_port
```

本 PR 同步增加原生 ABI 布局离线检查、Windows/Linux/Android Native
构建缓存指纹及补丁应用顺序。Windows CI 编译不代表 A/B 的真实切网实验完成。
