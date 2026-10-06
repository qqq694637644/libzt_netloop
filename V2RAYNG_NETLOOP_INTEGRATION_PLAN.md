# v2rayNG + NetLoop Integration Plan

## 1. Goal

Build a unified Android user experience while keeping the two data planes independent.

Target traffic path:

```text
Android app traffic
    ↓
v2rayNG VpnService / TUN
    ↓
Xray
    ↓
SOCKS5 127.0.0.1:1080
    ↓
NetLoop
    ↓
libzt / ZeroTier
    ↓
Remote NetLoop exit B
    ↓
Internet
```

The user should only need to operate the v2rayNG UI during normal use.

NetLoop may remain a separate APK and process. The two projects do not need to share a runtime, JNI boundary, or packet-processing implementation.

---

## 2. Architecture decision

### 2.1 Keep data plane independent

v2rayNG owns:

- Android `VpnService`
- TUN / hev-socks5-tunnel
- Xray runtime
- Android per-app routing
- DNS and Xray routing
- user-facing UI

NetLoop owns:

- local SOCKS5 server
- SOCKS5 TCP CONNECT
- SOCKS5 UDP ASSOCIATE
- libzt node
- ZeroTier identity/state
- peer/default-exit routing
- physical network change handling
- runtime reset

The integration boundary for traffic remains SOCKS5 over localhost.

Do not replace SOCKS5 with Binder, JNI, shared memory, or a custom packet protocol.

### 2.2 Unify only the control plane

v2rayNG becomes the user-facing controller for NetLoop.

Control responsibilities:

```text
v2rayNG UI
    ↓
Android IPC
    ↓
NetLoop control service
    ↓
NetLoop foreground runtime
```

IPC carries only:

- start
- stop
- configuration
- current status
- status notifications

It never carries TCP or UDP content.

### 2.3 Separate APKs are the primary design

Primary deployment:

```text
com.v2ray.ang
com.libzt.netloop
```

Advantages:

- NetLoop and v2rayNG can be upgraded independently.
- .NET Android and Xray/Go do not need to share a runtime.
- NetLoop keeps its dedicated `:netloop` process and existing libzt process-final lifecycle.
- v2rayNG remains a normal Gradle/Kotlin Android application.
- localhost SOCKS5 remains the stable data-plane ABI.

A single APK may be explored later, but it is explicitly not part of the first implementation.

---

## 3. Hard constraints

The first implementation must follow these rules:

1. No NativeAOT/JNI rewrite of NetLoop.
2. No attempt to load C# directly inside the v2rayNG daemon process.
3. No Binder/AIDL transport for network content.
4. No new ZeroTier peer discovery.
5. No dynamic route protocol between the two apps.
6. No Xray-underlay mode in v1.
7. NetLoop is the final Internet egress path.
8. NetLoop physical sockets must bypass the v2rayNG VPN.
9. v2rayNG is the only normal user-facing UI.
10. NetLoop identity/state remains owned by NetLoop.

---

## 4. User experience

The v2rayNG UI adds one NetLoop section.

Minimum settings:

```text
NetLoop

Enabled          [ on/off ]

Network ID       8056c2e21c000001
Default Exit     172.26.0.254

Status           Ready
Node             abcdef1234
Managed IP       172.26.0.10
SOCKS            127.0.0.1:1080
```

For the first version:

- SOCKS host is fixed to `127.0.0.1`.
- SOCKS port defaults to `1080`.
- NetLoop state directory is internal to the NetLoop app.
- overlay TCP/UDP ports keep their existing defaults.
- only `networkId` and `defaultExit` are required for the normal UI.
- optional peers may remain hidden from the initial UI unless they are required by an actual deployment.

Normal operation:

```text
User presses Connect in v2rayNG
    ↓
v2rayNG binds NetLoop control service
    ↓
v2rayNG sends desired NetLoop config
    ↓
NetLoop starts
    ↓
v2rayNG waits for READY
    ↓
v2rayNG starts Xray
    ↓
v2rayNG establishes VPN/TUN
    ↓
Xray proxy outbound points to 127.0.0.1:1080
```

Stop sequence:

```text
User presses Stop
    ↓
v2rayNG stops VPN/Xray
    ↓
v2rayNG requests NetLoop stop
```

For v1, NetLoop stops together with v2rayNG. Do not add a "keep running" option until there is a concrete need.

---

## 5. NetLoop control protocol

### 5.1 Prefer Android Messenger/Binder control

Use an exported Android bound service backed by standard Android Binder/Messenger APIs.

Reason:

- Kotlin and .NET Android can both use platform `Messenger`, `Message`, and `Bundle`.
- no shared generated AIDL source is required across Gradle and .NET builds.
- requests are low-frequency control messages.
- the interface stays intentionally small.

Suggested service:

```text
com.libzt.netloop.NetLoopControlService
```

Suggested explicit bind target:

```text
package: com.libzt.netloop
service: com.libzt.netloop.NetLoopControlService
```

The existing runtime service remains non-exported.

### 5.2 Control protocol v1

Define a simple integer protocol version:

```text
CONTROL_API_VERSION = 1
```

Commands:

```text
GET_API_VERSION
GET_STATUS
START
STOP
REGISTER_STATUS_CLIENT
UNREGISTER_STATUS_CLIENT
```

Suggested START content:

```json
{
  "network_id": "8056c2e21c000001",
  "default_exit": "172.26.0.254",
  "socks_port": 1080
}
```

If peer configuration is required later, extend the same content with:

```json
{
  "peers": [
    "172.26.0.11"
  ]
}
```

Do not add compatibility layers for multiple historical schema versions. If the API version is incompatible, fail clearly.

### 5.3 Status model

Keep the public status model small:

```text
STOPPED
STARTING
READY
ERROR
```

Status content:

```json
{
  "api_version": 1,
  "state": "READY",
  "node_id": "abcdef1234",
  "primary_overlay_address": "172.26.0.10",
  "socks_host": "127.0.0.1",
  "socks_port": 1080,
  "last_error": null
}
```

A status callback is preferred over polling.

v2rayNG may still issue `GET_STATUS` after binding or reconnecting.

---

## 6. NetLoop Android changes

### 6.1 Add a runtime controller

Introduce one Android-side controller responsible for process-local lifecycle state.

Suggested file:

```text
src-csharp/NetLoop.Android/NetLoopRuntimeController.cs
```

Responsibilities:

- hold the current public status
- serialize START / STOP requests
- publish status changes
- persist the latest runtime configuration snapshot
- start the existing foreground `NetLoopService`
- stop the existing foreground `NetLoopService`
- expose READY / ERROR information to `NetLoopControlService`

Do not move the actual SOCKS/libzt runtime into the control service.

### 6.2 Add exported control service

Suggested file:

```text
src-csharp/NetLoop.Android/NetLoopControlService.cs
```

Responsibilities:

- accept explicit cross-app bindings
- implement control protocol v1
- send status callbacks
- reject malformed configuration
- forward lifecycle requests to `NetLoopRuntimeController`

The existing `NetLoopService` remains:

- foreground
- non-exported
- dedicated to `:netloop`

The control service should run in the same `:netloop` process so it can observe runtime status without introducing another IPC layer inside NetLoop.

### 6.3 Persist only a runtime snapshot

v2rayNG is the user-facing source of truth.

NetLoop persists the last accepted runtime configuration only so Android process/service restart can recover without requiring the UI process to remain alive.

Example internal file:

```text
files/netloop-runtime-config.json
```

NetLoop continues to own:

```text
state_dir/
identity.secret
identity.public
network state
libzt state
```

The user should not need to edit NetLoop configuration separately.

### 6.4 Reuse existing runtime

The current flow remains conceptually unchanged:

```text
LibztNode
    ↓
NetLoopRuntime
    ↓
LocalSocksServer 127.0.0.1:1080
    ↓
Default exit peer
```

Do not fork a second runtime implementation for plugin mode.

Standalone UI and v2rayNG control must call the same runtime controller.

### 6.5 Existing process-final shutdown

Current NetLoop intentionally kills its dedicated process after the service is destroyed because libzt finalization is process-final.

Preserve that rule.

Expected STOP behavior:

```text
v2rayNG STOP
    ↓
NetLoopControlService requests runtime stop
    ↓
NetLoopService destroys runtime
    ↓
:netloop process exits
    ↓
Binder disconnects
    ↓
v2rayNG records NetLoop as STOPPED
```

A Binder disconnect during an intentional STOP is normal.

---

## 7. v2rayNG changes

### 7.1 Add NetLoop settings

Add preferences for:

```text
PREF_NETLOOP_ENABLED
PREF_NETLOOP_NETWORK_ID
PREF_NETLOOP_DEFAULT_EXIT
PREF_NETLOOP_SOCKS_PORT
```

Do not duplicate ZeroTier identity/state in v2rayNG.

### 7.2 Add NetLoopPluginManager

Suggested file:

```text
V2rayNG/app/src/main/java/com/v2ray/ang/netloop/NetLoopPluginManager.kt
```

Responsibilities:

- detect whether `com.libzt.netloop` is installed
- bind/unbind the control service
- verify `CONTROL_API_VERSION`
- send START/STOP
- wait for READY
- receive status changes
- expose state to UI/service lifecycle
- surface a concise failure reason

It must not know libzt internals.

### 7.3 Add NetLoop UI

The v2rayNG settings page should provide:

- enable/disable
- network ID
- default exit
- current plugin state
- current node ID
- current Managed IP
- last error

Normal users should not need to open the NetLoop app.

If NetLoop is missing:

```text
NetLoop plugin is not installed.
```

For the personal-use version, a simple error is enough. No marketplace/download/update framework is required.

---

## 8. Xray configuration

### 8.1 NetLoop is the final outbound

When NetLoop mode is enabled, the effective Xray `proxy` outbound becomes SOCKS5 localhost.

Conceptual Xray outbound:

```json
{
  "tag": "proxy",
  "protocol": "socks",
  "settings": {
    "servers": [
      {
        "address": "127.0.0.1",
        "port": 1080
      }
    ]
  }
}
```

UDP must remain enabled end-to-end.

The flow is:

```text
Xray TCP
    ↓ SOCKS CONNECT
NetLoop

Xray UDP
    ↓ SOCKS UDP ASSOCIATE
NetLoop
```

### 8.2 Do not add a new Xray protocol

NetLoop is not a new Xray wire protocol.

Do not add:

```text
EConfigType.NETLOOP
custom Xray transport
custom Xray core patch
```

Use the existing SOCKS outbound implementation.

### 8.3 Injection point

NetLoop mode should be applied at runtime config assembly time.

The selected v2rayNG remote server profile is not used as the actual egress while NetLoop mode is enabled.

The UI must make this explicit so the user is not misled into thinking a VLESS/VMess/Trojan node is active.

A clean v1 behavior is:

```text
NetLoop enabled
    → replace runtime TAG_PROXY with localhost SOCKS outbound

NetLoop disabled
    → normal existing v2rayNG outbound behavior
```

Do not introduce proxy-chain semantics in v1.

---

## 9. VPN recursion prevention

This is mandatory.

NetLoop's libzt physical sockets must not be captured by the v2rayNG VPN.

With two APKs:

```text
com.v2ray.ang       → bypass VPN
com.libzt.netloop   → bypass VPN
normal apps         → captured by VPN
```

Modify `CoreVpnService.configurePerAppProxy()` so the NetLoop package is always outside the VPN when NetLoop mode is enabled.

Behavior by mode:

### Global VPN mode

Add NetLoop to disallowed applications.

### Bypass-app mode

Ensure NetLoop is in the bypass/disallowed population.

### Allow-list mode

Ensure NetLoop is never added to the allowed population.

Do not call both Android `addAllowedApplication` and `addDisallowedApplication` on the same VPN builder.

This package bypass is the primary recursion prevention mechanism for the two-APK design.

---

## 10. Startup sequencing

The startup order is part of correctness.

Do not start Xray/VPN first and hope NetLoop becomes ready later.

Required order:

```text
1. User requests connect.
2. v2rayNG binds NetLoop control service.
3. v2rayNG verifies control API version.
4. v2rayNG sends current configuration.
5. v2rayNG sends START.
6. NetLoop enters STARTING.
7. NetLoop starts libzt and local SOCKS.
8. NetLoop reports READY.
9. v2rayNG builds Xray config using localhost NetLoop SOCKS outbound.
10. v2rayNG establishes VPN/TUN and starts Xray.
```

If NetLoop does not become READY within a bounded startup timeout:

- do not start the VPN data path
- surface NetLoop startup error
- leave the system in a stopped state

No hard-recovery state machine is required.

---

## 11. Android service restart behavior

Android may recreate the v2rayNG VPN service or NetLoop process.

The design must tolerate:

### NetLoop process death

v2rayNG Binder disconnects.

If v2rayNG is still supposed to be connected:

```text
binder disconnect
    ↓
rebind
    ↓
GET_STATUS
    ↓
restart NetLoop if needed
    ↓
wait READY
```

Do not silently continue sending Xray traffic into a dead localhost SOCKS port.

### v2rayNG process/service restart

When VPN service restarts and NetLoop mode is enabled:

```text
bind NetLoop
    ↓
GET_STATUS
    ↓
if READY: reuse
if STOPPED/ERROR: START
    ↓
wait READY
    ↓
start Xray/VPN
```

---

## 12. Failure behavior

Keep error handling simple.

Examples:

### NetLoop APK missing

```text
NetLoop plugin is not installed.
```

### API mismatch

```text
NetLoop control API version mismatch.
```

### Invalid network ID

```text
Invalid NetLoop network ID.
```

### No Managed IP

Display the NetLoop startup error and do not start Xray/VPN.

### Default exit invalid/unreachable

NetLoop remains the component that owns routing/connect failure behavior.

v2rayNG only shows the reported error/status.

Do not duplicate NetLoop routing diagnostics inside v2rayNG.

---

## 13. Security scope

This is a personal-use integration, not an enterprise plugin framework.

Do not add:

- plugin marketplace
- remote plugin installation protocol
- certificate infrastructure
- keystore-based plugin signing workflow
- multi-tenant authorization
- generic third-party plugin ABI

The control service should use an explicit component name.

If caller filtering is desired, keep it lightweight and only allow the expected v2rayNG package/UID.

---

## 14. Repository changes

### NetLoop repository

Expected primary files:

```text
src-csharp/NetLoop.Android/NetLoopControlService.cs
src-csharp/NetLoop.Android/NetLoopRuntimeController.cs
src-csharp/NetLoop.Android/NetLoopService.cs
src-csharp/NetLoop.Android/AndroidConfig.cs
src-csharp/NetLoop.Android/Properties/AndroidManifest.xml
```

Potential documentation changes:

```text
README.md
V2RAYNG_NETLOOP_INTEGRATION_PLAN.md
```

### v2rayNG repository

Expected primary files/areas:

```text
V2rayNG/app/src/main/java/com/v2ray/ang/netloop/
V2rayNG/app/src/main/java/com/v2ray/ang/service/CoreVpnService.kt
V2rayNG/app/src/main/java/com/v2ray/ang/core/CoreServiceManager.kt
V2rayNG/app/src/main/java/com/v2ray/ang/core/CoreConfigManager.kt
V2rayNG/app/src/main/java/com/v2ray/ang/handler/SettingsManager.kt
V2rayNG/app/src/main/java/com/v2ray/ang/AppConfig.kt
V2rayNG/app/src/main/res/xml/
V2rayNG/app/src/main/res/values*/strings.xml
```

Exact UI files should be selected from the current settings structure when implementation begins.

---

## 15. Implementation phases

### Phase 0 - Manual proof of path

No architecture changes.

1. Start current NetLoop APK.
2. Confirm SOCKS on `127.0.0.1:1080`.
3. In v2rayNG, manually create/use SOCKS outbound pointing to NetLoop.
4. Ensure `com.libzt.netloop` is excluded from VPN capture.
5. Confirm TCP traffic exits from remote ZeroTier exit B.
6. Confirm UDP traffic exits from remote ZeroTier exit B.

This phase proves the complete data path before adding IPC/UI work.

### Phase 1 - NetLoop control service

Implement:

- runtime controller
- control protocol v1
- START
- STOP
- GET_STATUS
- status callbacks
- persisted runtime config snapshot

Do not modify SOCKS/libzt routing behavior.

### Phase 2 - v2rayNG control integration

Implement:

- NetLoopPluginManager
- install detection
- bind/rebind
- API version check
- start/wait READY
- stop
- status presentation

At the end of this phase, v2rayNG can fully operate NetLoop without opening the NetLoop app.

### Phase 3 - Automatic Xray outbound

When NetLoop mode is enabled:

- replace runtime proxy outbound with localhost SOCKS
- preserve existing v2rayNG routing/DNS logic
- keep TCP and UDP enabled
- prevent Xray start until NetLoop is READY

### Phase 4 - VPN bypass correctness

Update per-app VPN handling so `com.libzt.netloop` is never captured by the v2rayNG VPN while NetLoop mode is active.

This phase is mandatory before considering the integration usable.

### Phase 5 - UI cleanup

Once control integration is stable:

- make v2rayNG the only normal UI
- optionally remove NetLoop launcher entry
- retain a minimal NetLoop diagnostic screen only if useful
- do not duplicate configuration screens

---

## 16. Build and validation policy

### CI

For implementation changes, CI only needs to prove build/package success for the affected projects.

NetLoop:

- Windows x64
- Linux x64
- Linux arm64
- Android arm64-v8a

v2rayNG:

- Android compile/package for the target ABI/flavor used by this fork

Do not require Android emulator E2E as part of routine CI.

### Manual physical-device acceptance

The useful final validation is on a real Android device.

Minimum acceptance cases:

1. NetLoop not installed -> v2rayNG gives a clear error.
2. NetLoop installed but stopped -> one Connect action starts both components.
3. NetLoop READY -> Xray uses `127.0.0.1:1080`.
4. TCP public IP is remote exit B.
5. UDP public IP is remote exit B.
6. Wi-Fi -> cellular switch:
   - NetLoop sees the physical network change.
   - old NetLoop sessions are reset.
   - new TCP/UDP sessions recover.
   - no VPN recursion occurs.
7. cellular -> Wi-Fi switch behaves the same.
8. Stop in v2rayNG stops Xray/VPN and NetLoop.
9. Restart v2rayNG service while NetLoop is already READY -> v2rayNG rebinds and continues correctly.
10. Kill NetLoop process while v2rayNG is active -> v2rayNG detects disconnect and re-establishes the plugin before resuming traffic.

---

## 17. Explicit non-goals for v1

Do not add the following in the first integration:

- single-APK packaging
- NativeAOT C# library
- JNI NetLoop API
- custom Xray transport
- Xray-underlay/proxy-chain NetLoop mode
- multiple NetLoop instances
- multiple simultaneous ZeroTier exits
- dynamic peer discovery
- plugin marketplace
- generic third-party plugin SDK
- QoS
- adaptive routing
- automatic exit selection
- metrics subsystem
- complex recovery state machine

The v1 product is intentionally only:

```text
v2rayNG
    → localhost SOCKS5
    → NetLoop
    → ZeroTier exit B
    → Internet
```

with one unified UI.

---

## 18. Completion definition

The integration is complete when all of the following are true:

- The user can configure NetLoop entirely from v2rayNG.
- The user can connect/disconnect entirely from v2rayNG.
- NetLoop does not need to be opened during normal use.
- NetLoop remains a separate C#/.NET Android runtime.
- Xray talks to NetLoop only through localhost SOCKS5.
- NetLoop physical sockets never recurse into the v2rayNG VPN.
- TCP and UDP both exit through the configured ZeroTier exit B.
- Network switching does not require manually reopening either app.
- Both projects can still be built and upgraded independently.
