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
- 不把 SOCKS5 层扩展成通用代理库；只维护 NetLoop 需要的最小 CONNECT /
  UDP ASSOCIATE、source validation、timeout/cancellation 行为，并用协议级
  E2E 冻结语义。
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

一个 Node 可能同时得到多个 Managed IP。第一版明确选择一个 deterministic canonical /
primary overlay address：**优先数值最小的 IPv4，否则使用数值最小的 Managed IPv6**。TCP/UDP
Agent 和 RouteSelector 都只使用这个地址；status 只暴露
`primary_overlay_address`。`default_exit` 是必填项，并在它不是本机时自动加入
effective peers；`--peer` 只用于额外的、需要直接访问的对端 primary overlay
address，不需要也不应该重复填写 `default_exit`。其它 Managed IP 不进入
NetLoop 路由模型。

## 5. 统一出网

统一出网是核心功能，不是附加功能。

每个 Node 必须配置：

    default_exit = 172.26.0.254

出口节点自身也填写自己的 primary overlay address。没有 `default_exit` 时直接拒绝启动，不回退到本机出网。

路由逻辑保持极简：

    if target is self Managed IP:
        connect local loopback
    else if target is an explicit direct peer Managed IP:
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

其中访问 C 的示例要求 C 的 primary overlay address 显式出现在 A 的
`--peer` 中；访问 default_exit B 不需要再把 B 重复写进 `--peer`。

### 5.1 域名解析

SOCKS5 请求中的 DOMAIN 类型尽量保持域名不变，交给最终出口节点解析。这样 A 不提前使用自己的 DNS 解析，B 作为 default_exit 时由 B 的 DNS 和出网环境决定结果。TCP 与 UDP 都遵守这一原则。

### 5.2 出口 Backend

第一版支持两种出口：

- DIRECT：出口节点直接使用 System.Net.Sockets 访问 Internet。
- UPSTREAM_SOCKS5：可选，将最终 Internet 请求继续交给本机已有 SOCKS5，例如 v2rayN。

这样可以保留当前统一出口习惯，又不会把节点间服务访问强制塞进 v2rayN。

## 6. SOCKS5 协议实现策略

Phase 0 已验证 `VpnHood.Core.Proxies 8.1.851` 的 SOCKS5 server 行为成熟，但
其出站路径直接创建 `TcpClient/UdpClient`，没有可注入 libzt transport 的边界，
因此无法直接作为 NetLoop 的 peer server。

当前实现据此保留一个**范围受限的项目内 SOCKS 层**：

- TCP：标准 SOCKS5 CONNECT、NO AUTH / RFC1929 client auth、half-close。
- UDP：UDP ASSOCIATE、control TCP 生命周期、source validation、idle cleanup、
  FRAG != 0 拒绝。
- outbound transport 统一通过 NetLoop 的 TCP/UDP abstraction 注入。
- 不增加 BIND、UDP fragmentation、通用 ACL 等与 NetLoop 无关的 SOCKS5 能力。

这套行为不再为了“形式复用”而推翻；后续通过 Windows/Linux/Android E2E 和
协议断言冻结，包括 peer-local UDP response 的逻辑 Managed-IP source。

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

项目内 `NetLoop.Socks` 负责 UDP ASSOCIATE 握手、control TCP 生命周期、UDP
source validation、SOCKS5 UDP header 编解码、cancellation、timeout 和
association dispose；范围只覆盖 NetLoop 实际需要的行为。

### 8.2 节点间 UDP

Overlay UDP Agent 使用固定 libzt UDP 端口，例如 42043。

第一版优先保持 SOCKS5 UDP Datagram 语义。每个本地 SOCKS5 UDP association 可以使用独立的 libzt UDP socket，使远端用 source Managed IP + source UDP port 作为内部转发表 key，避免第一版引入 association-id multiplexing 协议。

远端 UDP relay table 只负责 NetLoop 内部转发映射，不重新实现 SOCKS5
association 生命周期；其状态使用短 idle timeout，并在 runtime reset 时整体丢弃。

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

Android 第一版的**发布与支持目标只有 arm64-v8a**。GitHub-hosted Android
emulator 无法可靠运行 arm64 native E2E，因此 CI 额外构建一个 x86_64-only
测试 APK；该 APK 只用于 emulator E2E，不发布、不作为产品支持 ABI。

固定一个 libzt commit，由 CI 构建各平台原生库，禁止开发机手工拷贝未知版本二进制。

## 11. 网络切换与秒级恢复

这是核心可靠性需求。原则只有一个：**网络一变，整个旧 NetLoop runtime 直接丢弃；不迁移、不修复、不保留旧 TCP/UDP 会话。**

### 11.1 触发来源

- Windows/Linux：.NET NetworkChange。
- Android：ConnectivityManager.NetworkCallback。

同一次切网可能产生多个 OS event，统一做约 250ms debounce。触发后不分析旧 transport 状态，也不等待 keepalive/libzt path event。
如果事件发生在 libzt node 已启动但业务 runtime 尚未建立的 startup 阶段，则直接通知
libzt 刷新 physical bindings，不等待 `StartAsync` 先成功返回。

### 11.2 Runtime Reset

网络变化后：

1. 停止当前 NetLoop 业务 runtime。
2. 直接丢弃所有本地 SOCKS TCP、peer TCP、UDP association、overlay TCP listener 和 overlay UDP socket。
3. 不等待旧 TCP 对端收到 EOF/RST；旧业务 runtime 被销毁即视为旧会话失效。
4. ZeroTier node、identity 和 network membership 保持运行，不做 node stop/start。
5. 通知 libzt 主机物理网络已变化，立即刷新 physical UDP bindings 和本地接口地址；该逻辑回移自 ZeroTierOne 1.12 的 reconnect 改进。
6. 使用同一 Managed IP 重新创建 overlay TCP/UDP Agent 和本地 SOCKS5 listener。
7. 标记 ready，只接受全新的 TCP CONNECT / UDP ASSOCIATE。

Windows/Linux/Android 使用同一语义：只重建 NetLoop 业务 runtime；libzt node 本身持续运行。这样既彻底丢弃旧应用会话，也避免 node 重启导致重新连接 ZeroTier roots/peers 的额外数秒开销。

不存在 Soft Recovery、Hard Recovery、NetworkEpoch、旧 UDP association 保活或 TCP session migration。

## 12. 恢复性能目标

| 项目 | 目标 |
| --- | --- |
| OS 网络变化事件进入 NetLoop | < 500 ms |
| runtime reset 开始 | debounce 后立即 |
| 旧 runtime / 旧连接 | 整体丢弃，不等待迁移或 drain |
| 新 runtime ready + fresh TCP/UDP 成功 | 目标 <= 3 秒 |
| 正常恢复依赖十几秒 TCP keepalive | 不接受 |

这些是工程目标，不是所有公网/NAT 环境的绝对保证；真实测试结果优先。

## 13. C# 项目结构

当前主实现已经完全迁移到 C#；旧 NetLoop C++ 业务实现已删除：

    src-csharp/
      NetLoop.Core/
        Routing/
        Runtime/
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
    routing.peers = 172.26.0.20,172.26.0.30
    routing.default_exit = 172.26.0.254
    egress.mode = direct | upstream_socks5
    reset.event_debounce_ms = 250

默认本地 SOCKS 只监听 loopback，避免无意暴露给本地 LAN。

## 16. 信任边界

- ZeroTier 网络授权是主要信任边界。
- 不做 NetLoop 自己的用户系统。
- Overlay Agent 只通过 libzt Managed IP 监听。
- 本地 SOCKS 固定监听 127.0.0.1。
- 允许已授权 Overlay Peer 访问节点本机服务。
- 第一版不做复杂 ACL；确有需要再加简单端口 allow-list。

## 17. 日志与诊断

统一结构化日志覆盖 node 生命周期、Managed IP、关键 route decision、OS network
change、runtime reset reason/reset_count/process_id、libzt api/socket error、UDP
association 和 egress failure。NetLoop 不解析或承诺输出 libzt peer 的
DIRECT/RELAY/UNREACHABLE path；runtime recovery 也不依赖这些 path 状态。

Windows 错误同时记录数值错误码；文本日志统一 UTF-8。

## 18. 实施阶段

### Phase 0 - 技术验证

- 验证成熟 SOCKS5 库和 UDP ASSOCIATE 生命周期。
- 验证 outbound transport 可替换性。
- 做最小 libzt P/Invoke wrapper。
- Windows/Linux 使用同一 identity 互相 TCP/UDP 通信。
- Android arm64 加载 libzt native library。
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
- FRAG != 0 明确拒绝。

### Phase 3 - 秒级 Runtime Reset

- Windows/Linux NetworkChange monitor。
- Android ConnectivityManager monitor。
- 约 250ms event debounce。
- 网络变化后整体丢弃旧 runtime。
- 同 identity/state 启动新 runtime。
- 新 TCP/UDP 只在新 runtime ready 后建立。
- 连续 reset 不产生残留进程/socket。

自动 E2E 使用确定性 reset 注入：连续 10 次 reset -> 同进程新业务 runtime ready -> fresh TCP 成功 -> fresh UDP ASSOCIATE 成功，每轮 Node ID 必须不变，单轮目标 <= 3 秒。真实 Wi-Fi/热点/蜂窝物理切网测试暂缓。

### Phase 4 - 跨平台交付

- Windows x64 主程序发布包：`netloop-win-x64.zip`（仅 exe/dll）。
- Windows x64 独立运行时包：`netloop-dotnet-runtime-win-x64.zip`（便携 .NET 10 x64 Runtime）；解压后通过 `DOTNET_ROOT_X64` 指定运行时目录。
- Android arm64 应用：`netloop-android-arm64.apk`。
- Linux x64/arm64 保留 CI 编译与测试，不参与手动 Release。
- 手动输入的 `vMAJOR.MINOR.PATCH` 版本号是正式发布的唯一版本源；Windows assembly version /
  `--version` 与 Android display version 由 workflow 注入，Android versionCode
  使用 `major * 1_000_000 + minor * 1_000 + patch`。
- native libzt 自动构建、缓存和打包。
- CI smoke/E2E；release workflow 通过 workflow_dispatch 输入版本号与 Windows/Android 平台选择，
  构建所选产物并创建/更新 `vMAJOR.MINOR.PATCH` GitHub Release。

## 19. 最低测试矩阵

TCP：peer 本机服务双向访问、多节点并发、default_exit 统一公网出口、域名由出口解析、peer/exit 不在线时快速失败。

UDP：UDP ASSOCIATE 建立/关闭、peer 本机 UDP、default_exit UDP、多 association 并发、control TCP close 后清理、idle timeout 清理。

Mobility：触发 runtime reset 后旧业务 runtime 被整体丢弃；libzt node 保持运行并立即刷新 physical bindings；fresh TCP 和 fresh UDP ASSOCIATE 恢复；不要求旧 TCP/UDP 会话继续存活；任何一端都不需要人工重启。

Identity：runtime reset 后 Node ID 不变，不删除 identity 和 network membership。

当前自动 E2E：

- Windows x64：两个独立 GitHub-hosted runner，TCP/UDP/default-exit + 10 次 reset。
- Linux x64：两个独立 GitHub-hosted runner，TCP/UDP/default-exit + 10 次 reset。
- Linux arm64：两个原生 arm64 GitHub-hosted runner，TCP/UDP/default-exit + 10 次 reset。
- Android：x86_64 emulator + Linux peer，TCP/UDP/default-exit + 3 次 reset；
  arm64-v8a 只做真实发布 APK 的 native ABI/打包验证。

这些 deterministic reset 验证“reset 一旦触发后的恢复”，不等价于真实
Wi-Fi/热点/蜂窝切换。真实物理网络切换仍需要 Windows 笔记本和 Android 真机验证。

## 20. 旧 C++ 清理

C++ 实现不作为 C# 的 recovery 设计依据。

旧 C++ 业务实现、旧 C++ CI/发布路径和仅为 C++ 服务的脚本已经删除。仓库只保留
C# native libzt 构建所需要的第三方源码、最小 CMake 入口和补丁。

## 21. 第一版完成定义

1. Windows、Linux、Android 均能加入同一 ZeroTier 网络。
2. 每个平台都能提供本地 SOCKS5。
3. A/B/C 可通过对方 Managed IP 访问对方本机 TCP 服务。
4. UDP ASSOCIATE 可访问对方本机 UDP 服务。
5. 普通 TCP/UDP Internet 流量可统一通过 default_exit。
6. default_exit 可直接出网，并可选接已有 upstream SOCKS5。
7. 常规切网不要求人工重启任何节点。
8. 网络变化后整体丢弃旧 runtime，并在目标 1-3 秒内让 fresh TCP/UDP 恢复。
9. TCP 旧连接直接失败，由浏览器/应用重建。
10. UDP 旧 association 直接失败，由应用重新建立。
11. 无 TUN/TAP、无系统路由、无远端 LAN 路由。
12. 日志能完整还原一次网络切换和恢复时间线。

## 22. 关键设计原则

1. 业务恢复优先于 DIRECT。
2. 新连接优先于保活旧连接。
3. OS network event 优先于 TCP timeout。
4. libzt 负责 overlay；NetLoop 不复制 ZeroTier。
5. SOCKS5 只维护 NetLoop 所需的受限协议层，不扩展为通用 SOCKS server；行为由
   跨平台协议/E2E 测试冻结。
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
- C# 直接兼容已有 `state_dir`，必须复用已有 identity、network membership 和 Node ID。启动时保留 identity/network state，清理 `peers.d` 并禁用 peer cache。

### 23.2 Overlay 路由

- NetLoop 不做 CIDR / Managed Route routing，也不查询 libzt route table。
- 每个 Node 只使用一个 deterministic primary overlay address：数值最小 IPv4 优先，否则数值最小 IPv6。
- 本机 primary address -> 本机 loopback；其它本机 Managed IP 对 NetLoop 不存在，
  若被配置成 `--peer` 或 `default_exit` 则启动失败。
- overlay peer 只由显式 `--peer` primary addresses 定义；非本机的 `default_exit`
  自动加入 peer 集合；本机 primary 不进入 peer 集合。
- 其他显式 peer primary address -> 直接连接该 peer Agent。
- Overlay Agent 收到请求后是最终处理节点：本机 primary address 访问 loopback；
  非 overlay 目标执行本机 egress；**不得再次转发给第三个 NetLoop 节点**。
- 本机 `default_exit == primary` 时，非 overlay 目标直接执行本机 egress，不经 libzt self-connect。
- 对某个 peer primary address 的 Agent 连接失败时明确失败，不改走 `default_exit`。
- `default_exit` 不可达时 fail closed，不自动 DIRECT。

### 23.3 SOCKS5 与 Egress

- 节点间 TCP 控制协议第一版使用标准 SOCKS5 CONNECT，不引入额外 tunnel protocol。
- DOMAIN 尽量保持到最终出口解析，避免入口侧 DNS 泄漏。
- `UPSTREAM_SOCKS5` 支持 no-auth 和 username/password。
- upstream TCP 使用 CONNECT。
- upstream UDP 必须使用 UDP ASSOCIATE；若 upstream 不支持 UDP，明确失败，禁止回退 DIRECT。
- 项目内只维护 NetLoop 所需的最小 CONNECT / UDP ASSOCIATE、source validation、
  bounded state、half-close、timeout/cancellation 行为；不扩展成通用 SOCKS 框架。
- UDP receive queue 使用固定容量 256，满时丢弃最旧 datagram，不允许无限增长。

### 23.4 Runtime Reset

- 不实现 Soft Recovery / Hard Recovery / NetworkEpoch / transport generation migration。
- 网络变化统一触发一次 runtime reset；OS 事件做约 250ms debounce。
- Desktop/Android reset request queue 都只保留一个 pending 事件，切网风暴不会排队
  连续重建 runtime。
- Desktop/Android 都只重建 NetLoop 业务 runtime，不重启 libzt node。
- reset 时关闭并等待所有 active TCP handler、UDP association 和 overlay listener 退出；不测试远端何时观察到 TCP RST。
- libzt 接收 host-network-changed 通知后立即执行 ZeroTierOne 1.12 同等的 binder refresh / local-interface rescan；identity 和 Node ID 保持不变。
- 当前自动验收连续执行 10 次 deterministic reset；每轮必须重新建立 fresh TCP 和 fresh UDP ASSOCIATE，目标 <= 3 秒。

### 23.5 第一版容量

- 至少支持 128 个并发 TCP tunnel。
- 至少支持 64 个并发 UDP association。

### 23.6 GitHub CI / E2E

- 编译结果以 GitHub Actions 为权威；本地构建仅用于开发便利，不替代 CI。
- 自动 E2E 使用 GitHub-hosted runner，并且每个节点必须位于独立 runner；禁止在一个 runner 上启动多个进程冒充跨机测试。
- Windows x64、Linux x64、Linux arm64 都运行真实跨 runner TCP/UDP E2E 和
  10-cycle deterministic reset；Android 使用 CI-only x86_64 APK 在 hosted
  emulator 上运行 E2E/3-cycle reset，arm64-v8a 只做 release build/package gate。
- 普通 push/PR/fork CI 继续使用无 secret 的 controller-less ZeroTier ad-hoc network。
- 暂不建设需要 ZeroTier controller/私有 network secret 的 trusted E2E。
- GitHub CI 无法证明真实物理网络 handover；发布前仍需在 Windows 笔记本与 Android
  真机执行 Wi-Fi -> 热点 / Wi-Fi -> 蜂窝连续切换测试并记录恢复时间。
- Build job 只构建一次，E2E jobs 只下载同一不可变 artifact，不重新编译。
- workflow 使用 `concurrency` + `cancel-in-progress: true` 取消同一 branch/PR 的过时 run。
- native libzt 使用 Ninja + sccache；同时缓存按 libzt commit + OS/arch + toolchain/build-input hash 生成的 native bundle。native bundle cache 命中时不递归拉取 native submodules、不重新配置/编译 libzt。
- NuGet 有实际外部依赖后使用 lock file 和 `setup-dotnet` dependency cache；不为了“看起来有缓存”去缓存无依赖 restore 或整个 `bin/obj`。
- 每次 CI 输出 native cache hit/miss、sccache hit/miss 和主要阶段耗时，用数据判断优化是否有效。
