# C# 跨平台重写计划

## 1. 项目目标

将当前 Windows/C++ 的 libzt TCP 字节流转发器重构为一个轻量、对等、跨平台的 SOCKS5 组网节点。

核心目标：

1. 每台设备运行同一个 NetLoop Node，不再区分固定 client/server。
2. 每台设备通过本地 SOCKS5 入口接入组网。
3. 设备可以通过对方的 ZeroTier Managed IP 访问对方本机服务。
4. 普通互联网流量统一交给指定的默认出口节点出网。
5. 同时支持 SOCKS5 TCP CONNECT 和 UDP ASSOCIATE。
6. 网络频繁切换时，优先丢弃旧连接并让新连接秒级恢复。
7. 至少支持 Windows、Linux、Android。
8. libzt 保持原生实现，C# 只负责封装、协议、路由、生命周期和平台集成。

本项目不是完整 VPN，也不复制 ZeroTier One 的虚拟网卡和系统路由能力。

## 2. 明确非目标

- 不做 TUN/TAP 或虚拟网卡。
- 不修改系统路由表。
- 不做 ICMP/ping。
- 不做远端局域网网段访问。
- 不做广播、mDNS、局域网自动发现。
- 不做旧 TCP 连接跨物理网络无缝迁移。
- 不做企业级用户系统、ACL、计费、QoS、控制台。
- 不重写 NAT 穿透、加密或 ZeroTier 节点发现。
- 不手写完整 SOCKS5 UDP ASSOCIATE 协议和生命周期。
- 不为了等待 P2P DIRECT 而阻塞业务恢复。

只有显式使用本地 SOCKS5 的应用进入 NetLoop。

## 3. 总体架构

每台设备都是对等 Node。任意节点都可以访问其他节点本机服务，也可以接受其他节点访问；配置为 default_exit 的节点额外承担统一互联网出口。

    Application
        |
    SOCKS5 127.0.0.1:1080
        |
    NetLoop Node
        |
    libzt TCP / UDP
        |
    Remote NetLoop Node
       / \
  Local Service   Internet

节点之间通过 ZeroTier/libzt 通信，但不要求操作系统安装 ZeroTier 虚拟网卡。

## 4. Managed IP 的语义

ZeroTier Managed IP 在 NetLoop 中作为逻辑节点地址使用，而不是操作系统真实网卡地址。

例如：

    A = 172.26.0.10
    B = 172.26.0.254
    C = 172.26.0.20

A 上应用配置本地 SOCKS5 为 127.0.0.1:1080。应用请求 172.26.0.254:8080 时：

1. A 判断目标是 Overlay Managed IP。
2. A 通过 libzt 连接 B 的 NetLoop Agent。
3. A 将 SOCKS5 CONNECT 请求交给 B。
4. B 发现目标 Managed IP 等于自己的 Managed IP。
5. B 将其映射成本机 loopback 服务，例如 127.0.0.1:8080。
6. B 使用普通系统 Socket 建连并开始 Relay。

因此通过 SOCKS5 访问 172.26.0.254:8080，逻辑上表示访问 B 本机 8080 服务。

IPv4 默认映射到 127.0.0.1，IPv6 默认映射到 ::1。后续可以提供简单配置覆盖本机服务映射地址，但不扩展为远端 LAN 路由。

## 5. 统一出网

统一出网是核心功能，不是附加功能。

每个 Node 可以配置：

    default_exit = 172.26.0.254

路由逻辑保持极简：

    if target is self Managed IP:
        connect local loopback
    else if target is an overlay Managed IP:
        send SOCKS request directly to that peer
    else if this node is the selected exit:
        connect target using local egress backend
    else:
        send SOCKS request to default_exit

示例：

    172.26.0.254:8080 -> B 本机服务
    172.26.0.20:3000  -> C 本机服务
    example.com:443    -> default_exit B -> Internet
    8.8.8.8:443       -> default_exit B -> Internet

### 5.1 域名解析

SOCKS5 请求中的 DOMAIN 类型尽量保持域名不变，交给最终出口节点解析。这样 A 不提前使用自己的 DNS 解析，B 作为 default_exit 时由 B 的 DNS 和出网环境决定结果。TCP 与 UDP 都遵守这一原则。

### 5.2 出口 Backend

第一版支持两种出口：

- DIRECT：出口节点直接使用 System.Net.Sockets 访问 Internet。
- UPSTREAM_SOCKS5：可选，将最终 Internet 请求继续交给本机已有 SOCKS5，例如 v2rayN。

这样可以保留当前统一出口习惯，又不会把节点间服务访问强制塞进 v2rayN。

## 6. SOCKS5 协议复用策略

不从零实现 SOCKS5 server、UDP ASSOCIATE 生命周期和报文细节。

首选候选库为 VpnHood.Core.Proxies。Phase 0 必须验证：

1. SOCKS5 server 是否完整支持 CONNECT。
2. 是否完整支持 UDP ASSOCIATE。
3. TCP control connection 与 UDP association 生命周期是否正确绑定。
4. 是否允许注入或替换 outbound transport。
5. Windows、Linux、Android 是否均可运行。
6. 许可证是否符合最终分发方式。

如果 outbound transport 无法直接接入 libzt：

- 不重新手写 SOCKS5 UDP 协议。
- 优先 fork 或抽取其成熟的 parser、UDP ASSOCIATE、validation、timeout/cancellation 层。
- 在边界处接入 NetLoop 自己的 ITransport / IUdpTransport。

只有验证现有实现不可复用时，才重新评估其他成熟 SOCKS5 库。

## 7. TCP 数据面

本地入口：

    Application -> 127.0.0.1:1080 -> SocksFrontend -> RouteSelector

SocksFrontend 解析 CONNECT 请求，保留 ATYP、target host、target port。

Overlay TCP Agent 使用固定 libzt TCP 端口，例如 42042。

访问其他节点本机服务：

    A -> libzt -> B:42042 -> SOCKS CONNECT -> B loopback service

统一互联网出口：

    A -> libzt -> default_exit B:42042 -> SOCKS CONNECT target -> Internet

第一版优先复用标准 SOCKS5 CONNECT 作为节点间控制协议，不创建额外 tunnel protocol。

## 8. UDP 数据面

UDP 必须支持 SOCKS5 UDP ASSOCIATE。

### 8.1 本地 Association

成熟 SOCKS5 库负责 UDP ASSOCIATE 握手、control TCP 生命周期、UDP source validation、SOCKS5 UDP header 编解码、cancellation、timeout 和 association dispose。NetLoop 不复制这套状态机。

### 8.2 节点间 UDP

Overlay UDP Agent 使用固定 libzt UDP 端口，例如 42043。

第一版优先保持 SOCKS5 UDP Datagram 语义。每个本地 SOCKS5 UDP association 可以使用独立的 libzt UDP socket，使远端用 source Managed IP + source UDP port 作为内部转发表 key，避免第一版引入 association-id multiplexing 协议。

远端 UDP relay table 只负责 NetLoop 内部转发映射，不重新实现 SOCKS5 association 生命周期；其状态使用短 idle timeout，并在 transport generation 变化时整体失效。

访问 B 本机 UDP 服务：

    A SOCKS UDP -> 172.26.0.254:5353
    A -> libzt UDP -> B:42043
    B -> 127.0.0.1:5353

访问 Internet UDP：

    A -> libzt UDP -> default_exit B -> native UDP -> Internet

域名目标尽量保持到出口节点解析。

第一版不实现 SOCKS5 UDP fragmentation，FRAG != 0 明确拒绝或丢弃并记录日志。

## 9. libzt Native Wrapper

C# 不重新实现 libzt，只建立薄封装层：

    NetLoop.Libzt
      LibztNative
      LibztNode
      LibztTcpSocket
      LibztUdpSocket
      LibztEvent
      LibztNetworkInfo

只封装 NetLoop 真正需要的 ABI：

- Node：init/start、stop、join、状态、Managed IP、event callback。
- TCP：socket、bind/listen/accept、connect、read/write、shutdown、close、keepalive、nodelay。
- UDP：socket、bind、sendto、recvfrom、close。

原则：

- libzt fd 永远不伪装成 System.Net.Sockets.Socket。
- Native socket 与 libzt socket 使用两个清晰独立的 abstraction。
- P/Invoke/unsafe/native 细节限制在 NetLoop.Libzt。
- Core 只依赖接口，不直接调用 zts_*。

## 10. 跨平台 Native 打包

最低目标平台：Windows x64、Linux x64、Linux arm64、Android arm64-v8a。

Android 第一版**只支持 arm64-v8a**，不构建、不发布、也不承诺 x86/x86_64/armeabi-v7a。

固定一个 libzt commit，由 CI 构建各平台原生库，禁止开发机手工拷贝未知版本二进制。

## 11. 网络切换与秒级恢复

这是核心可靠性需求。原则是：网络一变，旧 tunnel 立即作废，新连接立即重新建立；不迁移旧 TCP。

### 11.1 NetworkEpoch

维护全局单调递增 NetworkEpoch。每个 TCP tunnel 和 libzt UDP transport 都记录创建时 epoch。检测到网络变化后 epoch++，所有旧 epoch transport 立即失效。

### 11.2 触发来源

至少监听：

- Windows/Linux：.NET NetworkChange。
- Android：ConnectivityManager.NetworkCallback。
- libzt peer/path 状态事件。
- 明确的 read/write/connect failure。

OS 网络变化是主要快速触发器；libzt peer/path event 是第二信号；keepalive 只做兜底。

### 11.3 Soft Recovery

网络变化时：

1. 对 OS event 做短 debounce，合并同一次切网产生的多事件。
2. epoch + 1。
3. 立即关闭所有旧 libzt TCP tunnel。
4. 关闭或替换旧 libzt UDP transport socket。
5. 不等待 DIRECT。
6. RELAY 可用就立即恢复业务。
7. 新 CONNECT 每次使用 fresh zts socket。
8. 使用短间隔重试，避免 stale socket 长时间阻塞。

初始重试节奏建议：0ms、150ms、300ms、500ms、800ms。Soft Recovery 总窗口目标约 2 秒，最终通过真实切网测试校准。

### 11.4 Hard Recovery

如果系统网络已经可用，但 Soft Recovery 窗口内 libzt 仍不能建立新的有效 peer connection：

1. 当前 epoch 最多触发一次 Hard Recovery。
2. stop libzt node。
3. 使用同一 identity/state 重新 start。
4. 重新 join 原 network。
5. identity 和 Managed IP 保持稳定。
6. network ready 后立即允许 fresh connect。
7. 设置短 cooldown，避免 restart storm。

Hard Recovery 是自动化的 libzt 重启，用来消除过去需要人工重启 A/B 才恢复的问题。

### 11.5 不等待 DIRECT

允许先通过 RELAY 恢复业务，再等待新的 DIRECT path。业务恢复优先于 P2P path 最优化。

### 11.6 TCP 与 UDP

TCP：epoch 变化后旧 tunnel 立即关闭，由浏览器/应用收到 EOF/reset 后重建；不尝试 session migration。

UDP：本地 SOCKS5 control TCP 和 association 尽量保持，只替换底层 libzt UDP transport；切网期间允许丢少量 UDP 包。

## 12. 恢复性能目标

| 项目 | 目标 |
| --- | --- |
| OS 网络变化事件进入 NetLoop | < 500 ms |
| 旧 TCP tunnel 开始清理 | 立即 |
| fresh libzt connect 开始 | < 300 ms |
| RELAY 已可达时业务恢复 | 约 1-2 秒 |
| 常规 Wi-Fi/热点/蜂窝切换 | 目标 1-3 秒 |
| 需要 Hard Recovery | 目标 3-5 秒 |
| 正常恢复依赖十几秒 TCP keepalive | 不接受 |

这些是工程目标，不是所有公网/NAT 环境的绝对保证；真实测试结果优先。

## 13. C# 项目结构

新实现与旧 C++ 并存一段时间：

    src-csharp/
      NetLoop.Core/
        Routing/
        Recovery/
        Transport/
        Sessions/
      NetLoop.Socks/
      NetLoop.Libzt/
      NetLoop.Host/
      NetLoop.Android/

Core 不依赖 UI；Android 生命周期只存在于 Android Host；SOCKS5 库通过 adapter 隔离；libzt native 细节只存在于 NetLoop.Libzt。

## 14. 平台实现

Windows：第一阶段 CLI，网络变化使用 .NET NetworkChange。

Linux：第一阶段 CLI + 可选 systemd service；先使用 .NET NetworkChange，真实测试不够再补平台专用 monitor。

Android：使用 .NET for Android，长期运行 Node 使用 foreground service；网络变化使用 ConnectivityManager.NetworkCallback。UI 只负责配置、启停和状态展示。

## 15. 配置模型

第一版保持小而明确：

    network.id = <network-id>
    network.state_dir = ./state
    socks.listen = 127.0.0.1
    socks.port = 1080
    overlay.tcp_port = 42042
    overlay.udp_port = 42043
    routing.default_exit = 172.26.0.254
    egress.mode = direct | upstream_socks5
    recovery.soft_window_ms = 2000
    recovery.event_debounce_ms = 200
    recovery.hard_restart_enabled = true

默认本地 SOCKS 只监听 loopback，避免无意暴露给本地 LAN。

## 16. 信任边界

- ZeroTier 网络授权是主要信任边界。
- 不做 NetLoop 自己的用户系统。
- Overlay Agent 只通过 libzt Managed IP 监听。
- 本地 SOCKS 默认仅监听 127.0.0.1 / ::1。
- 允许已授权 Overlay Peer 访问节点本机服务。
- 第一版不做复杂 ACL；确有需要再加简单端口 allow-list。

## 17. 日志与诊断

统一结构化日志，至少包含 event、peer、connection_id/association_id、epoch、transport、target_host/target_port、api_rc/socket_error、elapsed_ms、DIRECT/RELAY/UNREACHABLE path。

必须记录 node 生命周期、Managed IP、peer path、OS network change、epoch、Soft/Hard Recovery、TCP retry、UDP association、default-exit routing、local-service routing 和 egress failure。

Windows 错误同时记录数值错误码；文本日志统一 UTF-8。

## 18. 实施阶段

### Phase 0 - 技术验证

- 验证成熟 SOCKS5 库和 UDP ASSOCIATE 生命周期。
- 验证 outbound transport 可替换性。
- 做最小 libzt P/Invoke wrapper。
- Windows/Linux 使用同一 identity 互相 TCP/UDP 通信。
- Android arm64 加载 libzt native library。
- 验证 libzt stop/start/join 可用于 Hard Recovery。
- 验证各平台切网事件速度。

退出条件：关键风险都有最小可运行 PoC。

### Phase 1 - TCP Node

- 单一 NetLoop Node。
- 本地 SOCKS5 CONNECT。
- peer Managed IP 路由。
- 访问远端节点本机 TCP 服务。
- default_exit 统一 TCP 出网。
- DIRECT egress。
- 可选 upstream SOCKS5 egress。
- 通过公网 IP 对比确认确实由 default_exit 统一出网。

### Phase 2 - UDP

- SOCKS5 UDP ASSOCIATE。
- libzt UDP transport。
- peer 本机 UDP 服务。
- default_exit UDP 出网。
- association cleanup。
- UDP transport epoch replacement。
- FRAG != 0 明确拒绝。

### Phase 3 - 秒级恢复

- NetworkEpoch。
- Windows/Linux NetworkChange monitor。
- Android ConnectivityManager monitor。
- libzt path event integration。
- Soft Recovery。
- Hard Recovery。
- restart-storm protection。

自动测试阶段使用确定性故障注入覆盖 transport generation 变化、peer 暂时不可达、进程/libzt 重启和连续 recovery。真实 Wi-Fi A -> Wi-Fi B、Wi-Fi -> 手机热点、Android Wi-Fi -> Cellular 等物理切网测试暂缓，不作为当前 CI 或第一阶段交付阻塞项。

每次记录 OS event、epoch 更新、旧 tunnel 关闭、fresh connect、第一次 SOCKS 成功、RELAY/DIRECT 变化的时间点。

### Phase 4 - 跨平台交付

- Windows x64 发布包。
- Linux x64 发布包。
- Linux arm64 发布包。
- Android arm64 应用。
- native libzt 自动构建和打包。
- CI smoke/E2E。
- identity/state 升级兼容策略。

## 19. 最低测试矩阵

TCP：peer 本机服务双向访问、多节点并发、default_exit 统一公网出口、域名由出口解析、peer/exit 不在线时快速失败。

UDP：UDP ASSOCIATE 建立/关闭、peer 本机 UDP、default_exit UDP、多 association 并发、control TCP close 后清理、idle timeout 清理。

Mobility：切网时旧 TCP 立即关闭、新 TCP fresh socket 恢复、UDP association 保持但底层 transport 换代、RELAY 可用时不等待 DIRECT、Soft Recovery 失败自动 Hard Recovery、任何一端都不需要人工重启、多次切网不积累 zombie socket/thread/task。

Identity：restart 后 Node ID 和 Managed IP 不变，Hard Recovery 不删除 identity 和 network membership。

当前自动 E2E 范围只做 **Windows x64 <-> Windows x64**，且两个节点必须运行在两个独立 GitHub-hosted runner 上。Linux/Android runtime E2E 暂不作为当前 gate。

## 20. 从当前 C++ 版本迁移

当前 C++ 版本作为已知可用 TCP 数据面、mobility 问题复现、E2E 行为和 libzt 参数参考。

重写期间：

- C++ 版本只做必要 bug fix，不再叠加大功能。
- 新 TCP/UDP/跨平台能力进入 C# 版本。
- 先让 C# 达到当前 C++ TCP 能力，再切换主实现。
- C# 通过同等或更强 E2E 后，再考虑归档旧实现。

## 21. 第一版完成定义

1. Windows、Linux、Android 均能加入同一 ZeroTier 网络。
2. 每个平台都能提供本地 SOCKS5。
3. A/B/C 可通过对方 Managed IP 访问对方本机 TCP 服务。
4. UDP ASSOCIATE 可访问对方本机 UDP 服务。
5. 普通 TCP/UDP Internet 流量可统一通过 default_exit。
6. default_exit 可直接出网，并可选接已有 upstream SOCKS5。
7. 常规切网不要求人工重启任何节点。
8. 常规切网恢复目标 1-3 秒，异常 path 卡死时自动 Hard Recovery。
9. TCP 旧连接允许直接失败，由浏览器/应用重建。
10. UDP association 在可行时保留，只替换底层 transport。
11. 无 TUN/TAP、无系统路由、无远端 LAN 路由。
12. 日志能完整还原一次网络切换和恢复时间线。

## 22. 关键设计原则

1. 业务恢复优先于 DIRECT。
2. 新连接优先于保活旧连接。
3. OS network event 优先于 TCP timeout。
4. libzt 负责 overlay；NetLoop 不复制 ZeroTier。
5. SOCKS5 使用成熟实现；NetLoop 不重复造协议状态机。
6. Managed IP 是 Node 身份和逻辑服务地址，不是 OS 虚拟网卡。
7. 非 Overlay 目标默认走统一出口。
8. 不访问远端 LAN，因此不引入 CIDR 路由系统。
9. 平台差异限制在 Native/Host 层。
10. 个人使用优先：简单、可诊断、可恢复，比企业级抽象更重要。

## 23. 已冻结的实现决策

以下决策已经确认，后续实现和 CI 以此为准，不再把它们当作开放问题。

### 23.1 Toolchain 与原生基线

- .NET SDK 固定为 `10.0.401`，通过根目录 `global.json` 严格锁定，不随 runner 漂移。
- C# Target Framework 为 `net10.0`。
- libzt ABI/行为基线固定为仓库 submodule commit `a707ea6ae0910efdc1125d04758c411e2e9ea4f9`。
- C# 使用该 commit 已提供的 `ZTS_ENABLE_PINVOKE` shared-library wrapper ABI，只在 `NetLoop.Libzt` 建立 NetLoop 所需的薄封装，不复制整套历史 SWIG managed wrapper。
- C# 直接兼容当前 C++ 使用的 `state_dir`，必须复用已有 identity、network membership 和 Node ID。继续沿用当前 C++ 的 mobility 策略：保留 identity/network state，启动前清理 `peers.d` 并禁用 peer cache。

### 23.2 Overlay 路由

- 只把当前 ZeroTier network 的**直接 Managed Route**视为 overlay 地址空间。
- 带 gateway/`via` 的 managed route 不作为 NetLoop overlay peer 路由；NetLoop 第一版不访问远端 LAN。
- Phase 0 已验证固定 libzt 的 `zts_core_query_route()`：它能返回 target/via，但当前实现只把 route target 转成裸 IP 字符串，不返回 prefix/netmask，因此无法可靠用 managed route 判断任意目标 IP 是否属于 overlay CIDR。第一版据此启用已批准的 fallback：使用**显式 peer Managed-IP 列表**，并把 `default_exit` 自动加入 peer 集合；禁止根据缺失的 CIDR 信息猜测。route query 仍用于诊断 direct/via，不作为地址空间归属的唯一判据。
- 本机 Managed IP -> 本机 loopback。
- 其他 overlay Managed IP -> 直接连接该 peer Agent。
- Overlay Agent 收到请求后是最终处理节点：本机 Managed IP 访问 loopback；非 overlay 目标执行本机 egress；**不得再次转发给第三个 NetLoop 节点**。
- 对某个 overlay Managed IP 的 Agent 连接失败时明确失败，不改走 `default_exit`。
- `default_exit` 不可达时 fail closed，不自动 DIRECT。

### 23.3 SOCKS5 与 Egress

- 节点间 TCP 控制协议第一版使用标准 SOCKS5 CONNECT，不引入额外 tunnel protocol。
- DOMAIN 尽量保持到最终出口解析，避免入口侧 DNS 泄漏。
- `UPSTREAM_SOCKS5` 支持 no-auth 和 username/password。
- upstream TCP 使用 CONNECT。
- upstream UDP 必须使用 UDP ASSOCIATE；若 upstream 不支持 UDP，明确失败，禁止回退 DIRECT。
- Phase 0 对 `VpnHood.Core.Proxies 8.1.851` 的验证结论：其 SOCKS5 server 已具备 CONNECT、UDP ASSOCIATE、source validation、bounded state、half-close 等成熟行为，但 server 出站路径直接创建 `TcpClient/UdpClient`，没有可注入 libzt transport 的接口。因此 NetLoop 不直接套用 server；TCP 先实现最小标准 CONNECT adapter，UDP Phase 复用/抽取其成熟 association、validation、timeout/cancellation 语义并接入 NetLoop transport abstraction。

### 23.4 Recovery

- 继续继承当前 C++ 已验证的 fresh zts socket、TCP_NODELAY、keepalive、half-close 和 backpressure 语义。
- Hard Recovery 首先尝试同进程 `stop -> start -> join`。
- 如果 native libzt 无法在限定窗口内可靠恢复，允许自动重启整个 NetLoop 进程作为最终保险；必须复用同一 `state_dir`，不得删除 identity/network membership。
- 当前自动验收中：常规 recovery 做 20 次确定性故障注入，要求 p95 <= 3 秒；强制 Hard Recovery 做 10 次，要求 p95 <= 5 秒；任何一次需要人工干预即失败。
- recovery stress 目标为 100 轮后 active tunnel/association/native-socket 计数回到基线，不允许持续积累 zombie 资源。

### 23.5 第一版容量

- 至少支持 128 个并发 TCP tunnel。
- 至少支持 64 个并发 UDP association。

### 23.6 GitHub CI / E2E

- 编译结果以 GitHub Actions 为权威；本地构建仅用于开发便利，不替代 CI。
- 自动 E2E 使用 GitHub-hosted runner，并且每个节点必须位于独立 runner；禁止在一个 runner 上启动多个进程冒充跨机测试。
- 当前完整 E2E 只做 Windows x64 <-> Windows x64。
- 普通 push/PR/fork CI 继续使用无 secret 的 controller-less ZeroTier ad-hoc network。
- 暂不建设需要 ZeroTier controller/私有 network secret 的 trusted E2E。
- 暂不把真实 Wi-Fi/热点/蜂窝物理切网纳入自动或人工 release gate。
- Build job 只构建一次，E2E jobs 只下载同一不可变 artifact，不重新编译。
- workflow 使用 `concurrency` + `cancel-in-progress: true` 取消同一 branch/PR 的过时 run。
- native libzt 使用 Ninja + sccache；同时缓存按 libzt commit + OS/arch + toolchain/build-input hash 生成的 native bundle。native bundle cache 命中时不递归拉取 native submodules、不重新配置/编译 libzt。
- NuGet 有实际外部依赖后使用 lock file 和 `setup-dotnet` dependency cache；不为了“看起来有缓存”去缓存无依赖 restore 或整个 `bin/obj`。
- 每次 CI 输出 native cache hit/miss、sccache hit/miss 和主要阶段耗时，用数据判断优化是否有效。
