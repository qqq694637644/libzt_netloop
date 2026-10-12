# NetLoop 长期运行、路由安全与离线运维

此指南对应 PR #3 第二至第四阶段，基于 PR #2 的 Controller 动态路由。
**不创建 Windows 服务、不安装进程守护、不创建计划任务、不自动
重启 NetLoop 或 ZeroTier。** 原有 Android 前台应用生命周期不变。
应用更新直接从 GitHub Releases 下载对应版本，手动替换程序文件。

## 二：Controller 路由与 Agent 安全

应用每 30 秒重新读取 libzt native 网络配置；同时响应 Controller
地址/路由事件。只在取得**完整、有效**的查询结果后发布路由快照。
原生网络条目临时消失时保留上一版本用于日志和诊断，但不继续
用它为**新连接**选路；路由会明确 Reject，不会兜底到 B 的
`--default-exit`。配置恢复后新请求重新开放。

每个节点**恰好一个 Controller 分配的 IPv4 Managed IP**。没有 IP
时 libzt Node 继续存活并报告 `degraded`；同时出现多个 Managed IP
或只有 IPv6 属于明确的配置错误，不自动选择“Primary”。
Controller 仅更新路由时替换原子快照，**不动已有 TCP/RDP**。
真正撤销/更换这唯一 Managed IP 时，由一个 Runtime 所有者关闭旧
业务 Runtime（包括**本地 SOCKS UDP Association**），并在重新
核实唯一新地址后创建新 Runtime；允许此罕见配置事件中断旧 TCP。
原生 Node/身份文件不重建或覆盖。普通 Wi-Fi/热点切换只通知 libzt
更新路径，**不能只因网络事件就中断旧 TCP**。

Windows/Linux `--status-file` 每 15 秒原子更新
`updated_at_utc`、`last_network_query_utc`、`route_generation`、
`network_config_available`、活动绑定地址和当前
`phase=starting|ready|degraded|resetting|stopped`，同时记录
全局 TCP 活跃量、UDP 排队字节数、ACL 拒绝与 UDP 丢包计数。
`ready` 只代表业务端监听器就绪，不代表 A→C RDP 可达。
Android 使用**破坏式 Control API v2**，直接输出
`STARTING / READY / DEGRADED / RESETTING / STOPPED`，
额外报告 `reason`、`route_generation`、`updated_at_utc`；
没有 v1/v2 兼容协议，调用应用必须同步更新。

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
| `--max-tcp-tunnels` | 整个业务 Runtime 共 128 | 1–1024 |
| `--max-udp-associations` | 每入口 64 | 1–512 |
| `--relay-buffer-kib` | 每方向 64KiB | 4–256 |
| `--udp-queue-capacity` | 每个传输接收队列 256 包 | 16–4096 |
| `--udp-send-timeout` | 30 秒 | 1–120 |

Overlay UDP 接收后会交由**每个远端单独的处理队列**异步转发：
一个上游的慢发送/建连不会阻塞其他远端的原生接收循环。
每条关联限制排队数据量约 1MiB；整个 Runtime UDP 队列
合计还限制为 16MiB，超限直接丢包，不会无期限等待。
`system_receive`、`routing_receive`、
`overlay_forward` 累计在状态文件 `udp_queue_dropped` 中。
`overlay_udp_queue_drop` 有源地址和目标地址方便排障。

实验性的共享 FD Poller 及其参数已删除，**只维护原有
per-socket Poller**。原生 fd 批量 poll 的收益与断网安全性
未经 Windows/Android 实测，不能凭推测替换默认实现。
native TCP 写入不再跨异步等待固定缓冲区；UDP 接收使用共享缓冲池
而非每包重新分配约 64KiB 非托管内存。增加容量会放大实际
内存占用，个人小规模节点先保留默认值。

## 四：直接从 GitHub Releases 下载

新版本直接从 [GitHub Releases](https://github.com/qqq694637644/libzt_netloop/releases)
下载对应平台的软件包。退出正在运行的 NetLoop，手动替换程序，
然后按原有参数启动即可。ZeroTier `state` 目录不属于程序更新文件，
不要随意覆盖或删除。

本项目不提供自制升级器、旧版本快照、回滚工具、兼容兜底、
自动更新、Windows 服务或进程守护。

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
