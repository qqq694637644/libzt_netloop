# NetLoop 长期运行可靠性（PR #3）

这是基于 PR #2 的独立 stacked PR。仅对业务层的连接截止时间、
Runtime 失败恢复、关键监听任务监督、日志以及构建一致性进行加固。
不修改原生 ZeroTier Node 的生命周期、已有身份文件、Controller
路由逻辑以及 750ms / 3s 的 Peer negotiation 时序。

## 连接截止时间

| 阶段 | 默认值 | 可选 Windows/Linux 参数 |
| --- | --- | --- |
| 入站 SOCKS5 greeting、method、请求目标解析 | 15 秒 | `--socks-handshake-timeout 15` |
| 上游 CONNECT 请求至完整应答 | 25 秒 | `--socks-connect-reply-timeout 25` |
| 上游 UDP ASSOCIATE 建连、协商与完整应答 | 25 秒 | 同上 |

新参数的范围均为 1–120 秒。达到截止时间后关闭**尚未完成协商**
的连接，不允许一直占用 TCP capacity。成功建立的 TCP 数据流与
UDP association **不继承此握手定时器**。`--connect-timeout`
继续控制系统 TCP 建连；它并非 Peer 750ms 单轮连接窗口。

日志 `socks_handshake_timeout` 表示客户端未在期限内完成请求解析。
上游应答超时会转换为 `TimeoutException` 并清理对应连接。
这些超时均非 ZeroTier 网络路径失效的直接证据。

## 运行时恢复与健康监督

Windows/Linux Host 以及 Android Service 同时观察：

1. 本地 SOCKS accept task；
2. Overlay TCP Agent accept task；
3. Overlay UDP Agent receive task。

任一关键任务异常完成或意外正常退出，会记录
`runtime_critical_service_failed`，然后重新创建**业务 Runtime**。
Overlay TCP accept 连续失败 30 次（每次间隔约 100ms）也视作
监听器不可用，不会一直悄悄重试而继续呈现 Ready。
这个过程可能会中断已有 TCP/UDP；但**仅有 Controller 路由配置变化**
则只更新一个原子 RouteState，不会触发 Runtime 重置。
Wi-Fi/热点变化只通知 libzt 刷新底层网络路径，不无条件丢弃 RDP 会话。
Controller 真正撤销/替换节点**唯一 Managed IPv4**时，只有单一
RuntimeOwner 能关闭并创建业务 Runtime；旧本地 UDP Association
同样被关闭。没有新地址时原生 Node 保持存活并进入 `degraded`。

`LibztNode.JoinAndWaitAsync` 中 native Node 启动有截止时间，
Controller 暂时不可用则继续等待；明确拒绝加入是终态错误。
`RuntimeOwner` 在 **Node 启动等待**和**业务 Runtime 等待**
两个阶段都会及时消费物理网络变化通知：Node 尚未完成 `NetJoin`
时合并保存刷新请求，加入完成后再通知 libzt；人工 Reset 请求
保留到业务 Runtime 创建完毕处理。不额外运行守护进程。

`RuntimeRecovery` 等待 `ErrNoResult`、暂时没有 Managed IP，
以及**可明确分类**的原生 Agent socket/bind 网络错误：
`EADDRNOTAVAIL (99)`、`ENETDOWN (100)`、`ENETUNREACH (101)`、
`EHOSTUNREACH (113)`。这些代码来自本仓库固定的 libzt/lwIP ABI。
`EADDRINUSE (98)`、未知 errno、其它 native 服务失败、多个
Managed IP 和不支持地址族仍直接报错；**不对全部异常做无限重试**。
**按用户要求，不提供 Windows 服务、进程守护、自动重启或任务计划。**
需要人工诊断后手动重新启动程序；不要求删除 ZeroTier state 文件。

`netloop_ready` 仅意味着监听器已成功建立，**不是** C 端 RDP、
B 端 Web 或互联网出口已通过端到端探测。
`reset_elapsed_ms` 也不代表真正的新建 TCP 恢复耗时。

## 有界日志与自动轮转

`JsonLog` 使用最多 4096 条的异步有界队列，libzt 原生事件回调、
SOCKS/TCP/UDP 请求处理路径不再同步 Flush 控制台/磁盘。
队列拥塞时可能丢掉诊断信息，会输出
`json_log_records_dropped`，其中有 `info`、`error` 丢失计数。

Windows/Linux CLI 可选择：

```text
--log-file logs/netloop.jsonl --log-max-mb 16 --log-retain 5
```

磁盘最多保留一个当前文件和五个后缀文件（`.1` 至 `.5`），
通常约 96MiB。单条超长记录可轻微超出单文件限制。
如果磁盘写入失败，会尝试回退至 stdout 并记录错误。
未指定 `--log-file` 时维持 stdout JSONL；shell 自行重定向输出到
文件不会自动启用本项目的轮转。Windows Host 退出时尝试在
3 秒内 Flush 剩余日志。

日志包含远端地址和服务请求等诊断资料，请正确设置访问权限。
**绝不能删除、分享或提交 ZeroTier 的 identity.secret。**

## 原生构建缓存保护

Windows/Linux/Android 的 native cache key 新增了
`0004-network-cidr-queries.patch`、`0005-underlay-port-diagnostics.patch`
内容哈希。后者是只读 Binder 端口快照 ABI，不能与旧 native 包混用。
Windows/Linux managed 打包之前，验证 native library
manifest 对应的 libzt submodule SHA、CMake、build script、
全部 native 补丁哈希与当前 checkout 相同；缺失或不一致则
终止构建，避免缓存复用旧 ABI 的 libzt.dll/libzt.so。

此 PR 按约定只使用 Windows x64 CI（构建+纯 C# 离线回归），
不运行跨平台 E2E。
编译成功不能代替真实节点上 A→B / A→C 的新建 TCP 与旧 TCP
会话保活验证。

## PR #3 的第二、第三、第四阶段

第二阶段实现：30 秒周期性 native Controller 配置核验、查询失败
`Reject`（而非空路由或 B 兜底），唯一 Managed IPv4 变化时
重建业务 Runtime、不执行 Agent 热迁移；15 秒 status-file 心跳、
可选来源 CIDR / 目标 TCP/UDP 端口 ACL。
来源 ACL 基于 ZeroTier socket 已呈现的来源 IP，**不是独立的
ZeroTier Node ID 加密认证**；不能用它选择 Overlay 路由。

第三阶段实现：可配置 TCP / UDP association 容量、relay 缓冲大小、
UDP 接收队列、单包发送时限、每个 UDP 远端独立的有界队列
（最多 1MiB），整个业务 Runtime 共 16MiB 的队列字节预算，
超限丢包计数 `udp_queue_dropped`。删除未完成原生 fd 生命周期
验证的 shared Poller 和切换开关，保留单一 per-socket 实现。
不要盲目把容量提升到最大值。

本地 SOCKS5 UDP Factory 在创建失败时即使下游传输的
`DisposeAsync` 也失败，仍归还已获得的容量令牌，并保留原始
创建异常。Factory 关闭时逐个等待关联停止，清理某一个关联
失败也要继续关闭其余关联后再释放共享信号量；Overlay UDP
传输清理失败时仍归还未发送的排队字节预算。UDP 回复 Peer
集合只保留最近使用的最多 **64** 个地址，闲置超过 **120 秒**
的地址不再接受回复；这是响应来源限制，不参与 Controller 路由发现。

第四阶段只保留已有的日志轮转和离线 JSONL 探测日志的
小时/72h/7天统计。需要更新应用时直接从 GitHub Releases 下载，
自行替换新版本；不提供升级管理、旧版本快照或回滚机制。
**没有自动升级、后台守护、Windows 服务、计划任务。**
参见 [运维工具文档](long-term-operations.md)。

仍然没有依据调整的参数：10ms poll 周期、TCP Keepalive、
750ms/3秒 Peer negotiation 窗口。

## 小规模 A/B/C 网络的后续连接风暴诊断

旧版三端日志曾出现 B 的 128 条 TCP 额度饱和、A/C 大量失败重试，
以及 Peer 路径恢复 70～110 秒和 NODE_ONLINE/OFFLINE 约 226 秒
波动。修复业务 Runtime 的切网行为**不等于修复 native NAT**。

`tcp_session_phases` 将本地和 Overlay TCP 占用按
`Handshake / Connecting / Relay / UdpAssociation` 和实际来源
Managed IP 统计；当额度耗尽时保留每来源拒绝计数，但密集
JSONL 拒绝事件进行指数采样，避免二次日志风暴。
`peer_dials` 对同 Peer（包括默认出口 B）最多允许 2 个
正在进行 Peer TCP + SOCKS greeting、4 个等待；短暂失败冷却
最多 2 秒，**不等待最终目标 CONNECT 成功才释放名额**。
目标服务端拒绝/慢速响应/超时不会触发对健康 B/C Peer 的退避；
真实 Peer TCP/greeting 故障仍会冷却。已建立的 RDP Relay
**不受建连上限影响**，同时不盲目扩大 B 的 128 名额；
慢目标仍然会占 B 的普通 TCP 会话容量，直到其 CONNECT 终止。

入站 TCP 结束时，先完成本地/Overlay 连接异步 Dispose，
随后由单个 `TcpSessionTracker.Lease` 同步移除阶段统计、释放
共享 `SemaphoreSlim` 名额，避免在关闭中的连接上提前归还名额；
状态文件的 `active_tcp_tunnels` 和 `tcp_session_phases.Total`
共用同一采样结果。并发退出、异常退出和重复 Dispose 已有离线
回归检查。

Node Ready 后通知 libzt 更新物理路径时，若原生 API 返回失败，
唯一 RuntimeOwner 保留请求并在原进程内稍后重试，成功后停止重试；
不因切网通知而强行销毁原来的 TCP 会话。只读 native Binder
快照记录真正采样到的主/副 UDP 端口变化，与 Peer RELAY→DIRECT
和 Node ONLINE/OFFLINE 事件计算时间差；不根据这些事件本身
猜测公网 NAT 映射、实际 SOCKS 连通性或最终故障根因。
具体手动采样流程参见[三端切网诊断](network-handover-diagnostics.md)。

建议先按 72 小时、7 天两个时间窗口观察，并配合独立 PowerShell
connection probe；避免多个高风险参数同时调整。
