# PR #3：A/B/C 切网与连接风暴的手动定位

PR #4 在此方案之上新增 Native Binder / Wire UDP / Planet
只读取证时间线，参见 [Native Underlay 诊断日志](native-underlay-trace.md)；
它不改变本页所述 PR #3 的连接限流和恢复策略。

面向固定个人 ZeroTier 节点：A/C 的本地 SOCKS5 访问 B 默认出口，
A 可通过 Controller 直连 C 的 SOCKS5 Agent。以下只解释新的诊断和
人工验收，不安装服务、任务计划或持续探测程序。

## 改动的范围

- **B 侧 TCP 资源归因**：Runtime 仍共用原本的
  `--max-tcp-tunnels`（默认 128）额度。`tcp_session_phases`
  从实际业务流程统计 `Handshake`（请求未读完）、
  `Connecting`（正在连接上游）、`Relay`（已连通并转发）、
  `UdpAssociation`（已建立 UDP 控制连接），并按
  `local / overlay` 以及来源 IP（A/C 的 Managed IP）汇总。
  `RejectedLocal` / `RejectedOverlay` 与 `RejectedSources`
  记录容量拒绝来源；大量连续拒绝的 JSONL 日志采用指数采样，
  **内存中的计数保持精确**；`runtime_phase` 在状态变化时输出
  占用快照，容量拒绝事件记录当时的分类与来源。
  空闲、已关闭的连接不会继续计入。
- **A/C 侧 Peer 拨号**：整个业务 Runtime 中，对同一个 B/C
  Managed IP 同时最多 2 个 TCP 连接 + SOCKS5 greeting 协商，
  另可排队
  最多 4 个、最长等待 3 秒。队列满时明确拒绝，不无限积累；
  **仅 Peer TCP 建连或 greeting 失败**才采用
  250/500/1000/2000ms 上限的短暂冷却，Peer greeting 成功后清零。
  此时立即归还 Peer 拨号名额，**不等待 B/C 最终目标的 CONNECT
  回复**。目标端口关闭、拒绝访问、DNS/连接超时以及慢目标
  均不触发 Peer 失败退避，也不占用建连名额；目标 CONNECT
  仍受原本 25 秒应答超时保护。**已经建立的 RDP/SSH 也不限制
  Relay 时长或带宽**。其他 Peer 分开计数，
  路由决策和本地 loopback 不经过此限制。`peer_dials` 状态提供
  每个 Peer 的当前正在协商、排队、拒绝及冷却剩余毫秒。
  即使 Peer 阶段已经结束，目标 CONNECT 等待期间仍占有 B 的
  一个**普通 TCP 会话额度**；B 的 128 默认全局容量并未扩大，
  大量真正的慢目标仍可能触及该上限。
- **TCP 退出计数一致性**：本地/Overlay 入站连接完成异步清理后，
  同一个 `TcpSessionTracker.Lease` 才移除当前阶段统计并归还
  全局 `SemaphoreSlim` 容量；重复关闭不会重复释放名额。
  内存中的活跃连接数与 `tcp_session_phases.Total`
  使用**同一个统计快照**，`runtime_phase` 在阶段变化时输出。
  极短的“信号量已被 accept 获取、
  会话尚未注册”窗口仍可能存在，不夸大为整个数据面的原子快照。
- **物理路径刷新失败**：Node Ready 期间
  `zts_node_network_changed` 返回失败，会在原本的单一
  `RuntimeOwner` 中保留并每 500ms 重试，直到成功接受；
  **不重启 libzt Node**；Windows PR #5+ 的确认切网属于破坏式
  业务 Runtime episode，一轮最多重建一次。
- **Native 取证日志（显式启用）**：PR #7 已删除过时的托管
  `libzt_udp_bound_ports_changed`、`libzt_peer_recovery_observed`
  和 `libzt_node_cycle_observed` 统计器。需要分析 Binder 端口、
  Peer 路径及 NODE_ONLINE/OFFLINE 时，启用
  `--native-trace --log-level info` 并使用
  `libzt_native_underlay_trace` 事件中的实际 Native 时间戳。

这些 native 事件不是外部 SOCKS 成功凭证：Peer 可能被判 DIRECT 却仍
无法访问 C:3389；端口日志是采样时刻（周期 Controller 查询通常 30 秒），
**可能漏掉很短的端口轮换**；NODE_OFFLINE 也不意味着本地应用当时一定
不可达。不要把事件间隔记成真正的 MTTR。

## 人工 A/B/C 验收

1. 在 A、B、C 三端手动运行**同一构建**，不要覆盖或删除 ZeroTier
   `state` 和 `identity`。使用有界 `--log-file` 保存事件，
   **不需要 `--status-file` 或周期状态文件**。
   Windows JSONL 路径示例：`--log-file logs/netloop.jsonl --log-max-mb 16 --log-retain 5`。
2. 在 A、C 各自的终端**手动**运行已有
   `tools/connection_probe.ps1`，例如指定 `-Node A -IntervalSeconds 2`，
   使采样窗口足够细。这个外部探测测量 SOCKS → 实际目标服务，
   而非只测 native NODE_ONLINE。手动结束探测即可。
3. 依次测试：正常新建 A→B、A→C TCP/RDP；A/C 切 Wi-Fi/热点；
   B 切网；短暂断网恢复。保留各端带 UTC 时间戳的 JSONL 日志。
4. 对照三端以下字段：
   - **`tcp_accept_rejected` / `runtime_phase.data.tcp_session_phases`**：容量拒绝时
     是哪个来源、握手/上游建连/Relay 哪一类占了 128 名额。
   - **`peer_dial_rejected`、`peer_dial_failure_backoff`、
     `runtime_phase.data.peer_dials`**：是否仍有单 Peer 并发洪峰；排队拒绝是否
     只影响新连接，而原 RDP 不会被本限速器主动断开。
   - **`libzt_physical_network_refresh_requested`、
     `libzt_udp_bound_ports_changed`**：切网请求后，Binder
     主/副端口是否变化；不能由此断言 NAT 外部映射必然变化。
   - **`libzt_peer_status` / `libzt_peer_recovery_observed` /
     `libzt_node_cycle_observed`**：70～110 秒 Peer 路径恢复与
     约 226 秒 Node 周期是否继续存在，出现时本机端口是否同时变化。
   - **外部 `connection_probe` JSONL**：从人工切网到首次 SOCKS
     目标成功的观察窗口，与 native 事件时间线按 UTC 对齐。
5. 若 70～110 秒仍存在，**先根据三端数据再定位**：
   本机 Binder 端口确实变化、Peer 报 RELAY 还是 DIRECT、
   还是单纯目标服务不可达。不要凭旧版日志直接修改 NAT/UDP
   端口轮换、Peer Cache、TCP Keepalive 或 10ms Socket Poller。

## 验证边界

新增的离线 C# 检查只证明：TCP 额度分类、并发退出时的容量归还
可重现；单 Peer 拨号/排队/冷却有界；目标拒绝连接、
两个慢目标与目标回复超时**不会导致 Peer 冷却或霸占拨号名额**；
真实 Peer TCP 失败仍可触发冷却；物理刷新失败保留请求；
可重现 226 秒 NODE 周期的计算公式。它们不启动 ZeroTier
Node，也不测真实 UDP NAT、RDP、72h/7 天长稳。

对本项目个人使用场景，Windows CI 在构建 x64 程序后直接运行
**不启动 native Node 的 C# 离线回归检查**；不要自动启用
Android/Linux CI 或远端网络 E2E。Native 补丁与托管
P/Invoke ABI 必须**同版本构建**，不可把旧版 `libzt.dll`
与新 C# 程序混用。
