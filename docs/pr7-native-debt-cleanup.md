# PR #7 — Native 技术债收敛（基于 PR #6）

## 已完成的目标

- **19 个增量补丁收敛为 3 个可重放补丁**：libzt 统一补丁、ZeroTierOne 统一补丁、Android P/Invoke 特有补丁。固定的原生子模块提交不变；Root/Relay 保底、Leaf Path revalidation、IPv6 DAD 和 Root HELLO/ACK 功能没有因合并补丁而被删除。
- **Native 诊断是显式 opt-in**：默认 `--log-level info` 只记录已有托管 JSONL；`none` / `error` 不安装 ZeroTierOne Trace callback，不启动 C# Native 消费线程。排障时加 `--native-trace --log-level info`，启用原有 240 字节 ABI、限频物理 UDP 和 Core 事件。
- 移除无仓库内业务调用的旧 UDP 端口查询 ABI、托管端口属性、额外 `NotifyPhysicalNetworkChanged()` 重复入口，以及仅用于 226 秒周期和 Peer 恢复耗时统计的内存状态。Node 初始化、Fatal、网络配置事件继续正常处理。
- **统一 Windows handover episode**：RuntimeOwner 是唯一分组者，传递 `handover_epoch` 给 libzt；Native 不自行另跑 6s/2s 分组。Native Binder 完成刷新后继续执行已有 Leaf 重验证与快速 Root 重连，Node identity/state 不变。
- **按精确本地绑定发送快速 Root HELLO**：Windows IP Helper 必须在新 IPv6 地址上返回 Preferred 且非 SkipAsSource；`InterfaceLuid` 与 Binder 的 IPv6 本地地址必须同时匹配，并选中实际 UDP socket。显式 socket 在重绑后失效时不再退化为跨接口广播。正常 ZeroTier 数据包路径不受此限制。
- **Root 重试更健壮**：每 Root 最多三次实际尝试，按真实认证 OK(HELLO) 停止；DAD/接口查询瞬态失败只延迟下次检查，不消耗剩余尝试，但最多等待 30 秒。Root 状态锁不包裹 Topology 查询或底层 send callback。
- **缓存指纹单一事实源**：`tools/native_cache_key.py` 直接读取两份 build 脚本的补丁顺序，结合 Git 固定的 libzt gitlink、CMake/构建脚本和 NDK 修订版生成短哈希；不再在三个 GitHub workflow 手工列出补丁哈希。

## 不改变的产品行为

- Windows 确认物理切网：旧 SOCKS/TCP/UDP Runtime 连接破坏式终止，重新创建业务 Runtime；不重启 ZeroTier Node、不重新 join。
- Controller Managed Routes 热更新保留现有 TCP，A→C 路由与 B 默认出口 fail-closed 规则不变。
- 保留严格单 Managed IPv4、SOCKS UDP、Upstream SOCKS5、ACL、PeerDial 2+4 和 B 容量限制。
- Root/Planet 现有中继/路径保底与 Leaf 重新验证继续存在；本 PR 不加探测流量、不调全局路径超时。

## 必须验证

1. 从固定、干净的 libzt + ZeroTierOne gitlink 可以重复应用 Windows/Linux/Android 对应补丁，Android 额外应用其 P/Invoke 兼容补丁。
2. Windows x64 / Linux x64、arm64 / Android arm64 原生库重新编译；Native ABI 和 C# 离线回归通过。不得使用旧 Native 缓存通过构建。
3. `--log-level none` / `error` 和默认 `info` 下 Native Core callback 不安装；显式 `--native-trace` 的 A/B 日志仍可解析。
4. Windows 双 Wi-Fi/热点实机 A/B：Root HELLO 仅从 LUID+绑定 IPv6 匹配的 socket 发出，`core_handover_root_ipv6_hello_authenticated` 能与实际提交匹配。真实业务恢复速度、Root/Relay/Leaf 退化行为与 PR #6 对照。

> 离线测试与 CI 只能证明编译、ABI 和源码路径正确，不能证明 Windows DAD/多网卡与真实 NAT 恢复速度未回归。

## 有证据后才继续的精简

- **Leaf revalidation / WHOIS**：保留原行为，直到保持 Root 快速重连不变的 A/B 对照证明删除后同等或更好。
- **删个人不使用的 Egress、ACL、SOCKS UDP、reset-command-file 或文件轮转**：是产品范围变更，不称为“死代码”，需先确认真实消费者。
- **升级 libzt / ZeroTierOne**：单开基线对照，不把升级与此次 Root 修复混在同一次回归中。
