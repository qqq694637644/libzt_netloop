# PR #7 — Native 技术债收敛（基于 PR #6）

## 已完成的目标

- **19 个增量补丁收敛为 3 个可重放补丁**：libzt 统一补丁、ZeroTierOne 统一补丁、Android P/Invoke 特有补丁。固定的原生子模块提交不变；Root/Relay 保底、Leaf Path revalidation、IPv6 DAD 和 Root HELLO/ACK 功能没有因合并补丁而被删除。
- **Native 诊断是显式 opt-in**：默认 `--log-level info` 只记录已有托管 JSONL；`none` / `error` 不安装 ZeroTierOne Trace callback，不启动 C# Native 消费线程。排障时加 `--native-trace --log-level info`，启用原有 240 字节 ABI、限频物理 UDP 和 Core 事件。
- 移除无仓库内业务调用的旧 UDP 端口查询 ABI、托管端口属性、额外 `NotifyPhysicalNetworkChanged()` 重复入口，以及仅用于 226 秒周期和 Peer 恢复耗时统计的内存状态。Node 初始化、Fatal、网络配置事件继续正常处理。
- **Windows handover episode 按真实出口分组**：RuntimeOwner 分别读取 IPv4、IPv6 最优路由的接口 ID（允许双网卡不同出口）、IPv4 源/网关、IPv6 前缀及 Wi-Fi BSSID。不同出口立即生成新 `handover_epoch`，同一出口的重复通知只刷新 Native Binder；不使用 6s/2s 时间阈值。Windows 入口立即抓取每个物理事件快照，完全去掉取消式 250ms 防抖；旧 `--reset-debounce-ms` CLI 已删除，Android 自身的 250ms 事件合并逻辑保留，Linux 维持短时间通知合并。
- **按精确本地绑定发送快速 Root HELLO**：Windows IP Helper 必须在新 IPv6 地址上返回 Preferred 且非 SkipAsSource；`InterfaceLuid` 与 Binder 的 IPv6 本地地址必须同时匹配，并选中实际 UDP socket。专用 Root HELLO 使用显式 `netloopWirePacketSendWithModeFunction` 参数要求指定 Socket，删除旧的 `localSocket` 62 位私有标记。普通 UDP 失效 Socket 的 Binder 回退不变。
- **Root 重试更健壮**：`(epoch, LUID, 本地 IPv6)` 是一个恢复目标。Native 每 epoch 最多执行 **6 批 Root 探测**，每批最多扫描 8 个 Root；Core 每 Root、每目标最多提交 3 次 HELLO，每 Root 跨目标最多 6 次。这里的「6」是每 Root / 探测批预算，**不是整个 epoch 最多 6 个 UDP 包**（理论上 8 个 Root 可产生最多 48 次提交）。出现新 Preferred IPv6 可在剩余预算内重新探测；多个 Root IPv6 稳定端点时轮换端点；只接受同一本地 Socket 上收到的认证 OK(HELLO)。DAD 瞬态失败不消耗预算，整轮 30 秒截止。
- **缓存指纹单一事实源**：`tools/native_cache_key.py` 直接读取两份 build 脚本的补丁顺序，结合 Git 固定的 libzt gitlink、CMake/构建脚本和 NDK 修订版生成短哈希；不再在三个 GitHub workflow 手工列出补丁哈希。

## 审查后修复：普通 UDP fallback 与真正关闭 Trace 热路径

- **P1 原生 UDP 回退语义已恢复**：普通 ZeroTier Peer、ECHO 和既有路径发包带着已失效 `localSocket` 时，继续退回 `Binder::udpSendAll()`。仅 `fastProbeRootIpv6AfterNetworkChange` 生成的专用 Root HELLO 会携带私有 Socket 标记；该 Socket 失效时直接拒绝发送，不能转移至其他 IPv6 接口。标记只用于同步 Core→libzt wire callback，不改变通用 Core/Socket API 语义。
- **Native Trace 关闭时不碰诊断性 Binder 查询**：UDP RX/TX 的 `describeUdpSocket()`、发送侧采样回调及绑定端口遍历都受 Native 开关保护；端口快照仅在开启 Trace 后更新。Binder 地址读取仍保留一次，用于必要的 Core 本地接口同步，不属于诊断冗余。
- **DAD 与 Root 重试**：在 Windows IP Helper 的多个 Preferred IPv6 中寻找真正具有同一 LUID 的当前 Binder socket；查询失败、Tentative 或已失效 Socket 推迟尝试，并在 30 秒截止后停止。认证 ACK 的 Root 状态记录提取为无 I/O 的私有策略，以便检验乱序到达和每 Root 的三次上限。
- **可重复测试**：`ci/test_native_handover_policy.cpp` 覆盖普通 Socket fallback、显式专用 Root 发送模式、IPv6/LUID 恢复目标、端点轮换、DAD、每 Root 六次预算、ACK 乱序及 Socket 不匹配。C# 覆盖 Wi-Fi→热点→Wi-Fi 的秒级往返、**间隔小于 250ms 的两次入口事件均入队**、双网卡 IPv4/IPv6 独立选路、第一次 IP Helper 失败但在没有第二次 OS 通知时自动重采集、并发回调、Runtime 创建与 Native 刷新重试。

## Windows 物理入口采集失败的边界

采集失败首先保留 Native Binder 刷新请求，但不把未知网络状态当作确定的物理切网去销毁 TCP。监控以 200ms 间隔最多重采集 5 秒；一旦有有效快照，就再送入 RuntimeOwner，交由真实出口身份决定 epoch 和 Runtime Reset。5 秒始终无法获得可用出口证据时结束本轮重采集并记录错误，**不会凭空创建新 epoch**；后续新的系统通知可再发起新轮次。Android 的网络事件与确定性的 `--reset-command-file` E2E 入口未改变。

### 仍需实机验证的边界

Windows 最优路由和 WLAN BSSID 是本机可观察的出口标识，不能严格证明运营商 NAT 映射保持不变；Wi-Fi 驱动无法提供 BSSID、多个出口使用相同本地地址/网关且路由不变时，仍可能无法仅凭被动快照区分。真实 DAD/双网卡下的 ACK Socket 绑定与恢复耗时必须对照 PR #6 实机复测，CI 不等于 2 秒恢复 SLA。

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
