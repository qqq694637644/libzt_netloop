# NetLoop 长期运行、路由安全与离线运维

此指南对应 PR #3 第二至第四阶段，基于 PR #2 的 Controller 动态路由。
**不创建 Windows 服务、不安装进程守护、不创建计划任务、不自动
重启 NetLoop 或 ZeroTier。** 原有 Android 前台应用生命周期不变。
全部发布/恢复动作由用户在程序关闭后**手动执行**。

## 二：Controller 路由与 Agent 安全

应用每 30 秒重新读取 libzt native 网络配置；同时响应 Controller
地址/路由事件。只在取得**完整、有效**的查询结果后发布路由快照。
原生网络条目临时消失时保留上一版本用于日志和诊断，但不继续
用它为**新连接**选路；路由会明确 Reject，不会兜底到 B 的
`--default-exit`。配置恢复后新请求重新开放。

Controller 撤销当前 Agent Managed IP：

1. 若新地址已分配，先在新 IP 上创建 TCP/UDP Agent 监听器。
2. 再停止旧 TCP accept，旧 TCP 已建立的会话尽量自然结束。
3. 原 IP 的 UDP 关联关闭；旧 TCP 监听者保留最多 16 个，超限
   将中断最早旧会话，以防止 IP 高频变化导致无限占用资源。
4. 新地址绑定失败时记录 `overlay_bind_migration_failed`，标记
   `degraded`，等待下一次 Controller 核验重试，**不重新创建 Node**。

Windows/Linux `--status-file` 每 15 秒原子更新
`updated_at_utc`、`last_network_query_utc`、`route_generation`、
`network_config_available`、`overlay_bind_assigned` 和当前
`phase=ready|degraded`，同时记录 ACL 拒绝与 UDP 丢包计数。
`ready` 只代表业务端监听器就绪，不代表 A→C RDP 可达。
Android 复用控制 API v1 的 STARTING/READY/STOPPED 字段；
配置失效/监听 IP 被撤销时报告 STARTING（保留诊断节点信息），
配置恢复后才改回 READY；不扩展旧 Binder 协议状态枚举。

### Agent 入站 ACL（可选）

```powershell
.\netloop.exe --network 52b337794fddbc8c --state-dir .\state `
  --default-exit 172.26.0.254 `
  --agent-allow-source 172.26.0.2 `
  --agent-allow-source 172.26.0.3 `
  --agent-allow-target-port 3389 `
  --agent-allow-target-port 8080
```

这些地址与端口仅是**例子**，不要把示例网段或节点地址写进产品代码。
允许来源可以是单个 IPv4/IPv6 地址或 CIDR；
允许目标端口可以是单一端口或范围（如 `8000-8099`）。
同类参数可重复。**若没有配置来源白名单，允许经 ZeroTier
网络授权的全部来源；若没有配置端口白名单，允许全部目标端口。**
这是显式的入站访问限制，和已经删除的 `--peer` 路由发现参数
完全不同。

TCP 未授权来源在 Agent 接入后拒绝并关闭；CONNECT 目标端口
不允许则回复 SOCKS5 0x02。UDP 不符合来源或目标规则直接丢弃。
日志 `agent_source_denied`、`agent_target_denied` 与状态
`agent_source_denied`、`agent_target_denied` 提供汇总。
来源 Managed IP **不是**加密验证的 Node ID：成员权限仍应在
ZeroTier Controller 里正确管理。ACL 不生成 Managed Routes，
不改变 /0 和经 via 网关的拒绝策略。

## 三：资源/缓冲区调优

| CLI 参数 | 默认值 | 允许范围 |
| --- | --- | --- |
| `--max-tcp-tunnels` | 每入口 128 | 1–1024 |
| `--max-udp-associations` | 每入口 64 | 1–512 |
| `--relay-buffer-kib` | 每方向 64KiB | 4–256 |
| `--udp-queue-capacity` | 每个传输接收队列 256 包 | 16–4096 |
| `--udp-send-timeout` | 30 秒 | 1–120 |
| `--libzt-poll-mode` | `per-socket` | `per-socket` / `shared` |

Overlay UDP 接收后会交由**每个远端单独的处理队列**异步转发：
一个上游的慢发送/建连不会阻塞其他远端的原生接收循环。
每条关联限制总排队数据量约 1MiB，队列满直接丢弃新包，
不会无期限等待。`system_receive`、`routing_receive`、
`overlay_forward` 累计在状态文件 `udp_queue_dropped` 中。
`overlay_udp_queue_drop` 有源地址和目标地址方便排障。

大量连接时可**手动**试验：

```text
--libzt-poll-mode shared --relay-buffer-kib 32 --udp-queue-capacity 128
```

`shared` 使 socket Poll 在一个异步任务里最多 64 个 FD 合批，
仍采用非阻塞 libzt native poll、保留 fd 锁避免 native socket
关闭竞态。该模式**仅编译验证、未经真实 Windows/Android
长连接、改网、并发实测**，所以默认依旧为 `per-socket`。
出现异常应手动回退为原默认模式。增加 TCP 连接容量、
UDP association 容量和缓存会增加内存占用，尤其不可将几个
上限同时调到最大。

## 四：不用服务/守护的手动升级与回滚

先人工停止 NetLoop，确保进程不存在；工具检测到仍有
`netloop.exe` 进程会拒绝修改。**不要删除或复制覆盖 state、
identity.secret、日志目录或 status 文件。** 下载受信任的官方
Release ZIP，核验其发布来源及 SHA256；可同时准备应用 ZIP 与
.NET runtime ZIP。该工具只接受 Release 根目录的平铺应用文件，
其他文件名、目录或路径穿越会被拒绝。

```powershell
& .\tools\manage_release.ps1 -Action Apply `
  -InstallDirectory C:\NetLoop `
  -BackupRoot D:\NetLoop-Release-Backups `
  -Packages 'C:\Downloads\netloop-win-x64.zip','C:\Downloads\netloop-dotnet-runtime-win-x64.zip'
```

完成后工具输出唯一的 `Rollback snapshot` 文件夹。你需要**手动**
重新运行原有的 `netloop.exe` 启动命令并验证 A/B/C 通路；
这个工具不会启动 NetLoop，也不会改变运行中的程序。

如果新版本不满足要求，人工停止程序，在确认安装目录中原升级
文件未被另一个发布更新修改后，回滚至指定的快照：

```powershell
& .\tools\manage_release.ps1 -Action Rollback `
  -InstallDirectory C:\NetLoop `
  -BackupRoot D:\NetLoop-Release-Backups `
  -Snapshot D:\NetLoop-Release-Backups\20261011T080000Z-EXAMPLE
```

`Rollback` 会校验已安装文件与快照中的哈希，若检测到其他更改
就拒绝覆盖。只处理升级时实际替换/新增的**应用文件**；
不会触碰 ZeroTier 节点身份和 state，也不会删除其他用户文件。
多版本升级的回滚应该从**最新一层快照**按顺序逆序执行。
若升级过程异常，工具尝试从预备快照回退；磁盘损坏等错误无法
保证自动恢复，需人工查看日志和快照。

## 离线 JSONL 长期探测统计

已有 `tools/connection_probe.ps1` 是独立的、只有用户自己运行时
才会进行的应用连通性探测。要分析已有记录，不需要发起新连接：

```powershell
python .\tools\analyze_connection_probes.py `
  --input '.\evidence\connection-probe-*.jsonl' `
  --json-out .\evidence\probe-summary.json `
  --csv-out .\evidence\probe-hourly.csv
```

按 `node`、目标分组，输出采样成功率、P50/P95 成功请求时延、
连续失败采样最大次数、失败后首次成功的**观察采样窗口**，
以及逐小时汇总。72h/168h 窗口从该组**最近一条采样时间**
向过去计算，不代表 72/168 小时都有连续数据。
如果两个相邻采样间隔 20 秒，**不能把 20 秒说成真实恢复耗时**。
这也不代表既有 RDP/SSH 长连接存活、真实网络 MTTR 或 ZeroTier
中继延时。

全部结果都只写本机指定文件；不安装自动探测服务、守护、
后台计划或消息通知。
