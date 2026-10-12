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

Native `0006-native-underlay-trace.patch`、`0007-core-underlay-socket-trace.patch`
和独立应用到固定 ZeroTierOne 子模块的 `0001-netloop-core-path-trace.patch` 添加只读
`zts_netloop_poll_underlay_event` P/Invoke ABI。NodeService 在最多 **512 条**
的固定上限 FIFO 中记录原生时间戳；C# 每 250ms 最多消费 128 条，
写入 JSONL `libzt_native_underlay_trace`。
丢弃时记录 `libzt_native_underlay_records_dropped`，不阻塞 TCP 业务以等待日志。

本 PR 后续增量补丁 `libzt/0008-recovery-decision-trace.patch` 和
`zerotierone/0002-netloop-recovery-decision-trace.patch` 进一步捕获
ECHO 往返、RENDEZVOUS 的来源、路由回退和 NAT 映射变化的决策证据。
**它们不改变定时器、Peer 选路、路径过期规则或发包数量。**

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
| `core_hello_sent` | core 生成 HELLO 并提交发送后 | `peer_id` 为目标 Node、`packet_id`、`endpoint` 为指定发送目标；`result=1/0` 为直接提交成功/失败，`-1` 为 Switch 异步路由发送、结果不可判定 |
| `core_hello_authenticated` | core 验证 HELLO 身份和 MAC 后 | `peer_id` 为身份已验证的发送方，`packet_id` 与实际物理 `endpoint` |
| `core_ok_hello_sent` | core 回复 OK(HELLO) 之后 | `hello_request_packet_id` 关联原请求，`result` 为该包的底层发送提交状态 |
| `core_ok_hello_authenticated` | core 验证 OK(HELLO) 的 MAC 且确认属于已期待的请求后 | `hello_request_packet_id` 关联请求；这是 core 确实处理匹配的 HELLO 回复的证据 |
| `core_rendezvous_sent` | core 向两端分别发送 RENDEZVOUS 介绍 | `peer_id` 为被介绍的一方，`endpoint` 为实际发送给另一方的物理路径；`result` 为底层提交状态 |
| `core_rendezvous_authenticated` | core 收到已认证的上游 RENDEZVOUS 指示 | `peer_id` 为被介绍的节点、`endpoint` 为上游通告的候选 IP:port；`result=1` 允许并尝试、`0` 过滤拒绝、`-1` 无已知 Peer、`-2` 地址无效 |
| `core_upstream_online_decision` | ZeroTierOne 自己计算 ONLINE/OFFLINE，状态变化或离线期间定期记录 | `last_upstream_receive_native_ms` 为 core 收到任一配置上游的最后时刻、`upstream_age_ms` 为计算得到的年龄；`reason_flags` 为 `1=online`、`2=status_transition`、`4=am_upstream`、`8=received_upstream_before` |
| `core_peer_path_learned` | 身份已验证的 Peer 响应被 core 采用为新的直接路径时 | `peer_id`、`endpoint`、`packet_id`、`result` 为路径槽索引 |
| `core_echo_sent` | `Peer::attemptToContactAt` 在使用 ECHO 而非 HELLO 时 | `peer_id`、`packet_id`、候选 `endpoint`、`result=1/0` 是本机提交成功/失败 |
| `core_ok_echo_authenticated` | `IncomingPacket::_doOK` 处理已验证且有对应待答请求的 OK(ECHO) | `echo_request_packet_id` 与 ECHO 请求对应；它不等于 Overlay TCP 已连接 |
| `core_rendezvous_source` | `_doRENDEZVOUS` 处理已验证的介绍报文 | `peer_id` 是**发送介绍的上游 Node**；`endpoint` 是收到介绍的上游物理地址；`reason_flags & 1` 表示该节点被认为是上游。和 `core_rendezvous_authenticated` 通过相同 `packet_id` 关联候选端点 |
| `core_surface_observed` / `core_surface_previous` | `SelfAwareness::iam` 从 HELLO/OK 学到公网映射新值或变化 | 相同 `surface_observation_native_ms`、`peer_id`（上报者）关联新旧映射。flags 位：`1=trusted`、`2=known_before`、`4=public_IP_changed`、`8=public_port_changed`、`16=scope_path_reset_triggered`；`result` 为旧观测年龄毫秒，首次记录为 -1 |
| `core_peer_probe_or_path_removed` | `Peer::doPingAndKeepalive` 尝试旧路径、或因过期/优先级删除路径 | `endpoint` 是已知 Peer 路径，`peer_path_last_receive_native_ms` 是最后有效收包时间，`result` 是路径收包年龄；flags `1=full_HELLO`、`2=heartbeat`、`4=expired_removed`、`8=lower_priority_removed`、`16=no_probe_sent`、`32=path_alive`。仅 `16` 表示整轮无探测时，reference 是当前时间而非路径收包时刻 |
| `core_memorized_path_lookup` | `Peer::tryMemorizedPath` 每到重试期限调用外部地址查询 | `result=1` 命中，`0` 无保存候选；`endpoint` 为命中的候选地址，`memorized_lookup_native_ms` 为调用时刻 |
| `core_unknown_path_confirmation` | Core 收到已认证的非 OK 报文，但该物理路径尚未获准直接采用 | `reason_flags=0` 本次发起确认 HELLO，`1` 因最近 1 秒对同一路径已经尝试而跳过；`result` 为原始 ZeroTier verb |
| `core_switch_route_selected` | `Switch::_trySend` 对具体目标选择的物理下一跳 | `peer_id` 是**逻辑目的节点**；`endpoint` 是**真正选择的下一跳地址**；flags `1=nonexpired_peer_path`、`2=upstream_relay`、`4=expired_peer_fallback`、`8=no_route`、`16=unknown_peer`。每个目的约 5 秒一条，路径/选择变化则立即记录；`result` 是 ZeroTier verb |
| `core_upstream_probe_scheduler` | `Node::_PingPeersThatNeedPing` 判断是否暂缓或尝试上游心跳 | `peer_id` 为 Planet/Root、`last_upstream_full_hello_native_ms` 为上轮 HELLO；flags `1=suppressed_by_timer`、`2=has_remembered_path`、`8=attempted_contact`。若 flag1，`result` 是距下一轮允许探测的毫秒数，否则是 Peer ping 的 IPv4/IPv6 发送位掩码。每 Root 约 15 秒采样 |
| `core_scope_path_invalidated` | SelfAwareness 确定公网 IP 改变而准备清除同 scope 旧路径 | `endpoint` 为即将失效的旧路径；不涉及强制重建 libzt |
| `core_surface_expired` | `SelfAwareness::clean` 删除过期的公网映射记录 | `surface_last_seen_native_ms` 是最后的映射更新时间；`result` 是映射年龄毫秒 |

两个 UDP 事件新增 `local_endpoint`、`physical_interface`：
由 libzt Binder 在**真正选中并操作每个 UDP socket 时**读取，不是由目的地址猜出。
当 core 要求从所有绑定 UDP socket 发送时，每个 socket 有自己独立的发送记录；
日志对每个 `peer + 方向 + 对端/本地端点 + 接口` 每秒抽样一次。
接口名可能为空（例如 Binder 未能枚举、使用通配 `0.0.0.0/::` 绑定）。
新补丁也修复了 Windows Binder 原先虽然读到 FriendlyName、却存入空字符串的
诊断缺陷；`physical_interface` 优先展示本地绑定的 Windows 网卡友好名称，
但不保证是该次 `sendto` 最终经过的外发网卡。
本地绑定不等于 OS 路由的最终出站接口，也不等于 NAT 公网地址，不能据此声称
报文已抵达网关或对端。

`binder_refresh_*` 的 `reason_flags` 为位组合：
`1=network_changed`、`2=sleep/wake`、`4=periodic`、
`8=secondary_port_changed`（completed 才有）、`16=node_offline`。
原生刷新“收到”不代表 Peer 就绪；`result=1` 也只证明 Binder **主端口在本地地址集合中**，
不等于外部 NAT 可达。

**协议安全边界：** `wire_udp_*` 从 ZeroTier 报文*未验证的固定头*提取
40-bit Node ID 与 64-bit Packet ID。报文内容不记录、不解密、不保存；
无法仅由这些 UDP 日志证明一个加密包就是 HELLO、OK 或某个 Peer 的认证响应。
与之不同，`core_hello_authenticated`、`core_ok_hello_authenticated`、
`core_rendezvous_authenticated` 在原生 core 完成现有认证之后产生，
不记录身份私钥、MAC、加密负载或任意完整数据包。
被丢弃/分片/过短的包也可能无法被采样。
成功的 `wire_udp_sent` 只证明本地 send API 接受，**不证明 B 收到**；
`wire_udp_received` 只证明 native socket 看到了报文，core 返回码并非
“HELLO 握手成功”。Peer Wire 日志对相同 Node ID + 方向最多每秒一条，
但物理端点变化、发送错误可触发额外记录；高流量未被逐包完整取证。
不同端 Packet ID **恰好都被采样**时可比较，不保证每次 TX 都有 RX 配对。

Planet 快照只提供现有 core Peer 路径信息；新增的
`core_upstream_online_decision` 则记录 core 本身以“最近一次任一上游接收 +
超时阈值 / 我是否是上游节点”计算 ONLINE 的实际输入。
它没有记录远端 Planet 为何没有回复 UDP，所以不能直接断定
“Planet 服务故障”或“某条公网 IPv6 路由丢包”。
端口也是本机实际 UDP Binder 地址，而不是路由器公网 NAT 映射。
因此本 PR 是调查工具，不把“226 秒周期”视为已修复。

## 2026-10-12 高浏览器负载复现与下一次判定

10:28:59～10:31:57 UTC，A→B 探针首次失败到恢复成功请求相隔约
175.6 秒，A 独立公网通道此间基本正常。A 的 487 条 SOCKS 请求中
存在约 370 条准入队列拒绝/等太久，B 无容量拒绝。
Native 侧 A/B 长时间重复使用旧候选端点，直到 10:31:57 认证路径恢复。
但仅凭这一轮**不能证明浏览器负载改变了 core 调度速度**，
也不能认定公共路由器 NAT 或 Planet 一定故障。

下一次同时保留高负载 A 与 B 的 Native 日志，按同一 Peer ID 和
`native_timestamp_utc` 聚合：

1. `core_switch_route_selected` 的 flag 是否持续为旧 Direct(1)、
   Root(2) 或过期 Peer(4)；若有无路径(8)，检查上游是否可用。
2. `core_upstream_probe_scheduler` 是否长期被计时器压制，及
   `core_peer_probe_or_path_removed` 对旧路径的年龄和删除原因。
3. 对同一 `packet_id`，按 `core_rendezvous_source` →
   `core_rendezvous_authenticated`，定位是哪个上游仍然推荐旧端点；
   查看 `core_surface_observed` 有没有仅公网端口变化、却没触发
   scope reset（flag8 有、flag16 无）。
4. `core_echo_sent` → 对端 `wire_udp_received` →
   `core_ok_echo_authenticated`，并结合已有 HELLO/OK 日志，
   判断是 UDP 物理丢失还是协议层未采纳回复。**Wire 仍是限频采样，
   不能把缺失的 Wire 采样等同于未收到任何数据包。**

此版本**仅修复 Windows 网卡名称日志缺失**，没有修改 libzt Peer 恢复算法。

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
