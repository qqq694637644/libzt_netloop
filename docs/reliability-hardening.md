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
这个过程可能会中断已有 TCP/UDP；但**只有 Controller 路由配置变化**
仍只更新快照，不会触发 Runtime 重置。

在暂时没有 Managed IP、socket/native I/O 错误或 listener 无法
立即创建时，`RuntimeRecovery` 每轮重新查询最新网络配置，
最多重试 12 次，使用 250ms、500ms、1s、2s、4s、
之后上限 5s 的退避。不中止或重新初始化 libzt Node。
期间记录 `runtime_creation_retry`，并在指定了 `--status-file`
时写入 `phase=degraded`、`attempt` 与 `max_attempts`。
重建成功写 `runtime_creation_recovered` 以及新的 ready 状态。

永久配置错误不应无限重试；12 次仍不能恢复则向进程管理器上报
失败。Windows 7×24 小时运行需要额外的**进程守护和失败重启**
（例如 Windows 服务管理器的恢复策略）。本 PR 不添加不受限的
自动重启循环，也不要求删除原来的 ZeroTier state 文件。

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
`0004-network-cidr-queries.patch` 内容哈希。
Windows/Linux managed 打包之前，验证 native library
manifest 对应的 libzt submodule SHA、CMake、build script、
全部 native 补丁哈希与当前 checkout 相同；缺失或不一致则
终止构建，避免缓存复用旧 ABI 的 libzt.dll/libzt.so。

此 PR 按约定只核对 Windows 编译 CI，不运行跨平台 E2E。
编译成功不能代替真实节点上 A→B / A→C 的新建 TCP 与旧 TCP
会话保活验证。

## 后续阶段（暂不在本 PR 修改）

- 10ms libzt polling 的 CPU、线程及长连接性能调优；
- TCP Keepalive 策略和 750ms Peer negotiation 优化；
- Managed IP 撤销时的平滑监听地址迁移；
- Controller 路由快照新鲜度、需要 via 的 Managed Route；
- UDP queue drop 指标、限速与独立转发队列；
- Overlay Agent 的成员/端口 ACL 和线上指标采集。

建议先按 72 小时、7 天两个时间窗口观察，并配合独立 PowerShell
connection probe；避免多个高风险参数同时调整。
