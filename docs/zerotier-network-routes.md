# ZeroTier Controller 动态路由

NetLoop 通过本机 libzt 中已经取得的 ZeroTier Controller 网络配置，
决定一个 SOCKS5 目标应连接哪个节点的 NetLoop Agent。**不使用固定
`172.26.0.0/24`、也不从当前 Managed IP 推测网络掩码。**

## 数据来源和 native ABI

libzt 的 `zts_net_info_t` 提供分配地址、掩码、Managed Routes、网关、
配置修订号。上游现有的 `zts_core_query_addr` /
`zts_core_query_route` 查询接口却只返回 IP 字符串，会丢失地址
和路由的掩码；因此本仓库提供
`patches/libzt/0004-network-cidr-queries.patch`，增加两个很小的
原生查询函数：

- `zts_netloop_query_addr_cidr` → 原始地址及掩码（如 `10.42.7.2/23`）
- `zts_netloop_query_route_cidr` → Managed Route 的完整 CIDR、
  `via` 网关、`flags` 和 `metric`

同一份查询在 libzt core lock 内完成，避免一个刷新里混入不同
Controller 版本的地址和路由。Android 和 Windows/Linux 的 native
构建脚本均应用此补丁。**仅替换 C# DLL 而未替换 native libzt
将无法运行这个版本。**

## 匹配规则

| 目标 | 新连接行为 |
| --- | --- |
| 本机任一 Managed IP | 本机 loopback 服务（IPv4/IPv6） |
| 显式 `--peer` 或默认出口 Agent 地址 | 访问对应 Agent，兼容已有配置 |
| 匹配 Controller 下发的本地直连前缀 / Managed Route（无 `via`） | 直接访问目标 IP 的 NetLoop Agent `:42042` / UDP Agent `:42043` |
| 匹配需要 `via` 的 Managed Route | **明确拒绝**；不转给 B，不猜测网关上有 NetLoop Agent |
| 其他目标 | 使用配置的默认出口 B（出口节点使用自身 egress） |

优先选择**最长匹配前缀**；相同长度时，Controller 的显式
Managed Route 优先于从本机地址生成的直连前缀。
`/0` 不被当作 Overlay Peer 网段，避免把公网地址都当成
NetLoop Agent。若 Controller 只配置了 `/32` 或 `/128`，
NetLoop 不会自作主张将其扩大成更大的子网。
**匹配到 Overlay 但 Agent 不可达时明确失败，不回退到出口 B。**

请注意：ZeroTier 下发一个网段，只证明目标符合该网络的地址/路由
配置，不证明每一个地址都属于已授权成员，也不证明它运行着
NetLoop Agent；因此最终必须用 Agent TCP/UDP 建连来验证。
Agent 仍不允许从 Overlay 收到的请求继续转发到第三个 Overlay Peer，
避免循环和意外中转。网关 `via` 的多跳转发属于未实现功能。

例如，假设 Controller 实际下发了直接连接的
`10.42.6.0/23`、本节点地址为 `10.42.7.2/23`，
本网络另一个运行 Agent 的节点是 `10.42.7.3`：

```text
本地应用 --SOCKS5--> NetLoop A
  target 10.42.7.3:3389
  A -> libzt TCP -> 10.42.7.3:42042
  Agent C -> 127.0.0.1:3389

  target example.com:443
  A -> 默认出口 B -> Internet
```

这里的网段仅是说明原理的**示例**，真实路由一律从
Controller 配置读取。

## 无中断动态刷新

libzt 的 `NETWORK_OK (213)`、`NETWORK_READY_IP4/IP6 (215/216)`、
`NETWORK_UPDATE (219)`、`ROUTE_ADDED/REMOVED (250/251)`、
`ADDR_ADDED/REMOVED (260–263)` 会触发读取最新的地址/路由快照。
native 回调只向容量为 1 的队列发送通知，不直接加 native core lock；
后台工作线程读取配置，并为每次成功更新分配单调递增的
`generation`，通过原子替换更新 `RouteSelector`。
连续多次控制器更新可合并，最终查询的是**最新完整快照**。

Windows/Linux Host 和 Android Service 都订阅了此事件。
它们不会因为 `219` 或地址/路由更新而触发 NetLoop reset、
重启 libzt、关闭 SOCKS5 listener 或强制断开已有 TCP。
新连接根据更新后的快照选路；已有 TCP 连接继续使用现有 socket。
**物理网络变化引起的原有 runtime reset 逻辑保持不变**，
不能将这两类事件混为一谈。

若 Controller 撤销了当前 Agent listener 所绑定的 Managed IP，
运行时发出 `overlay_bind_address_no_longer_assigned`。
当前不会在保持旧 TCP session 的同时热迁移监听地址；
这类真正的地址变更需要另外设计平滑监听器迁移。当前版本
只保证正常的**路由配置变化**不破坏既有连接。

## 如何检查

在 A 的 JSONL 日志中查看：

- `libzt_network_ready`：首次读取的 `assigned_prefixes` /
  `managed_routes` / `generation`
- `libzt_network_config_refreshed`：libzt event 触发的重新读取
- `overlay_routes_updated`：Host 成功替换活跃路由快照
- `route_decision`：访问远端 Managed IP 时应为 `OverlayPeer`，
  而不是错误的 `DefaultExit`
- `libzt_network_config_refresh_failed`：原生配置查询失败；
  此时保留上一份有效路由表，而不是推测新的网段

在 B/C 的 Agent 日志中可用
`tcp_accept_completed` 和 `socks_connect_request`
确认新建 TCP 是否到达正确目标；参见
[双端关联诊断](connection-probe.md#tcp-%E5%BB%BA%E8%BF%9E%E4%B8%8E-agent-accept-%E6%97%A5%E5%BF%97%E5%85%B3%E8%81%94)。

注意使用 SOCKS5 的应用与直接向操作系统 TCP/IP 栈拨号不同；
本项目没有创建 ZeroTier 虚拟网卡或改变 Windows 路由表。
