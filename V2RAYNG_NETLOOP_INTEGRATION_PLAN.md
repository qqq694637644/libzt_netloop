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
- DNS interception/resolution plumbing
- Xray traffic capture and protocol conversion
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

When NetLoop mode is enabled, Xray does not choose the final Internet egress.
Captured TCP, UDP, and DNS traffic is sent to the NetLoop SOCKS endpoint, and
NetLoop owns peer routing and the final exit. No normal v2rayNG routing rule is
inherited in NetLoop mode, including direct, block, balancer, regional, private
IP, or process rules. Xray has one TCP/UDP catch-all to NetLoop.

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
8. NetLoop physical sockets must remain outside the v2rayNG VPN.
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

Advanced
  Direct Peers   172.26.0.20, 172.26.0.30

Status           Ready
Node             abcdef1234
Managed IP       172.26.0.10
SOCKS            127.0.0.1:1080
```

For the first version:

- SOCKS is fixed to `127.0.0.1:1080`.
- NetLoop state directory is internal to the NetLoop app.
- overlay TCP/UDP ports keep their existing defaults.
- `networkId` and `defaultExit` are required.
- `peers` is optional and contains only additional nodes whose primary Managed IP must be reachable directly.
- `defaultExit` is required on every node; the exit node sets it to its own primary Managed IP.
- a remote `defaultExit` is automatically included in NetLoop's effective peer set; do not duplicate it in `peers`.
- `peers` is directional: A -> C requires C's primary Managed IP in A's
  `peers`; C -> A separately requires A in C's `peers`. With an empty
  `peers` list, a node can directly address only itself and its configured
  default exit.
- do not add peer discovery, topology sync, or a controller-side peer database.

Normal operation:

```text
User presses Connect in v2rayNG
    ↓
v2rayNG binds NetLoop control service
    ↓
status = GET_STATUS

if status.api_version != CONTROL_API_VERSION:
    fail

start_status = START(api_version, network_id, default_exit, optional peers)

if START reports "stop before changing configuration":
    fail and require the normal Disconnect -> Connect lifecycle

if start_status.state == READY:
    start Xray and establish VPN/TUN

if start_status.state == STARTING:
    poll GET_STATUS every 250 ms
    READY → start Xray and establish VPN/TUN
    STOPPED or Binder disconnect → fail
```

This is the only v1 startup/rebind decision flow.

v1 does not live-reconfigure an already READY NetLoop runtime. Changing NetLoop
settings requires the normal disconnect/reconnect cycle, which provides a single
unambiguous configuration transition.

START itself is also frozen:

- STOPPED + START -> start with the supplied configuration.
- READY/STARTING + START with the same normalized configuration -> idempotent;
  return the current status and do not restart anything.
- READY/STARTING + START with a different configuration -> reject with
  "stop before changing configuration".

Configuration equality compares only the v1 controlled fields:
`network_id`, `default_exit`, and the normalized optional `peers` set.
No hot reload or implicit STOP/START is performed.

Production Android has no standalone runtime path. Every production runtime is
owned by the controlled in-memory configuration supplied through START.

GET_STATUS is not a configuration-consistency query. v2rayNG always follows a
successful version check with START(desired config), including when NetLoop is
already READY or STARTING. The idempotent START rule is the only v1 configuration
consistency check; do not add a config hash/fingerprint to status.

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
START
STOP
GET_STATUS
```

Suggested minimal START content:

```json
{
  "api_version": 1,
  "network_id": "8056c2e21c000001",
  "default_exit": "172.26.0.254"
}
```

An optional `peers` array may be added for additional explicit direct overlay
peers. It may also be omitted or empty. `default_exit` is effective
automatically when it is remote and must not be repeated in this array.

The request `api_version` must equal `CONTROL_API_VERSION`; otherwise reject
START immediately. Do not add compatibility layers for multiple historical
schema versions.

A v2rayNG-controlled START always constructs a fresh controlled runtime
configuration. Production Android has no persisted user-editable NetLoop
configuration. Controlled mode is fixed to:

```text
egress = direct
upstream_host = empty
upstream_user = empty
upstream_password = empty
```

This prevents accidental Xray -> NetLoop -> Xray proxy recursion and
intentionally does not implement proxy chaining in v1. Do not persist the
controlled configuration. v2rayNG is the source of truth and sends the full
desired configuration after every bind/rebind.

### 5.3 Status model

Keep the public status model small:

```text
STOPPED
STARTING
READY
```

Status content:

```json
{
  "api_version": 1,
  "state": "READY",
  "node_id": "abcdef1234",
  "primary_overlay_address": "172.26.0.10"
}
```

`GET_STATUS` is the single version-and-state query. v2rayNG calls it immediately
after binding and validates `api_version`, then always sends START with the
desired configuration. START either starts STOPPED, idempotently validates
READY/STARTING with the same configuration, or rejects a different running
configuration. While STARTING, poll every 250 ms and stop once state becomes
READY or STOPPED. After a Binder reconnect, repeat this same GET_STATUS -> START
flow. A Binder disconnect while NetLoop is expected to be running is itself a
lifecycle signal; no callback registry is required.

There is no stable public `ERROR` state. Synchronous request/validation failures
are returned directly as control errors. An asynchronous runtime failure stops
`NetLoopService`, the process-final `:netloop` process exits, and v2rayNG
observes Binder disconnect.

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
- answer current status queries
- keep the active controlled runtime configuration in process memory only
- start the existing foreground `NetLoopService`
- stop the existing foreground `NetLoopService`
- expose STOPPED / STARTING / READY information to `NetLoopControlService`

Do not move the actual SOCKS/libzt runtime into the control service.

### 6.2 Add exported control service

Suggested file:

```text
src-csharp/NetLoop.Android/NetLoopControlService.cs
```

Responsibilities:

- accept explicit cross-app bindings
- implement control protocol v1
- reject commands whose Binder calling UID does not belong to the expected v2rayNG package
- reject malformed configuration
- forward lifecycle requests to `NetLoopRuntimeController`

The existing `NetLoopService` remains:

- foreground
- non-exported
- dedicated to `:netloop`

The control service should run in the same `:netloop` process so it can observe runtime status without introducing another IPC layer inside NetLoop.

### 6.3 Controlled configuration is memory-only

v2rayNG is the user-facing source of truth.

NetLoop does not persist controlled `network_id`, `default_exit`, or `peers`.
Every bind/rebind follows GET_STATUS -> START(full desired config). If the
`:netloop` process dies, the in-memory configuration dies with it and v2rayNG
supplies the desired configuration again.

NetLoop continues to own:

```text
state_dir/
identity.secret
identity.public
network state
libzt state
```

The user should not need to edit NetLoop configuration separately.

Controlled starts use `START_NOT_STICKY`. Android must not independently revive
a controlled NetLoop runtime from stale desired state. The CI-only automation
entry point is also non-sticky and is not present in production builds.

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

Production Android exposes no standalone configuration UI, launcher START path,
or `netloop-config.json`. Android emulator automation, when explicitly built
with `NETLOOP_CI`, uses a CI-only broadcast receiver that constructs test
`HostOptions` and starts the same `NetLoopService`. Test automation must not
reintroduce a production fallback path.

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
PREF_NETLOOP_PEERS
PREF_NETLOOP_DEFAULT_EXIT
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
- issue GET_STATUS immediately after bind/rebind and validate its `api_version`
- always send START with `CONTROL_API_VERSION` and the desired configuration
  after the version check
- treat a different-running-configuration START rejection as an error; do not
  auto-STOP/START inside the Binder controller
- send STOP
- poll GET_STATUS while STARTING
- wait for READY
- expose state to UI/service lifecycle
- surface a concise failure reason

It must not know libzt internals.

### 7.3 Add NetLoop UI

The v2rayNG settings page should provide:

- enable/disable
- network ID
- default exit
- optional direct peer Managed IP list under an Advanced section
- current plugin state
- current node ID
- current Managed IP

Normal users should not need to open the NetLoop app.

If NetLoop is missing:

```text
NetLoop plugin is not installed.
```

For the personal-use version, a simple error is enough. No marketplace/download/update framework is required.

The v2rayNG manifest declares the known companion package explicitly:

```xml
<queries>
    <package android:name="com.libzt.netloop" />
</queries>
```

Do not add NetLoop-specific `QUERY_ALL_PACKAGES`; the integration needs only the
known companion package visibility contract. If the host app independently
needs broader package visibility for other existing features, that is outside
the NetLoop plugin contract.

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

### 8.3 Runtime config hook

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

### 8.4 Support both Android TUN engines

NetLoop mode supports both existing v2rayNG TUN engines. Do not force one engine
globally.

Xray TUN path:

```text
Android VPN/TUN fd
    ↓
Xray tun inbound
    ↓
Xray SOCKS outbound 127.0.0.1:1080
    ↓
NetLoop
```

HEV path:

```text
Android VPN/TUN fd
    ↓
hev-socks5-tunnel
    ↓
Xray internal SOCKS inbound 127.0.0.1:10808
    ↓
Xray SOCKS outbound 127.0.0.1:1080
    ↓
NetLoop
```

`PREF_USE_HEV_TUNNEL` selects the engine. In NetLoop mode both engines use the
same full-capture Android VPN routes and the same final NetLoop SOCKS outbound.
HEV must be configured with IPv6 enabled regardless of the normal
`PREF_IPV6_ENABLED` value, because NetLoop mode is always dual-stack. The
internal HEV-facing SOCKS inbound is fixed, loopback-only, unauthenticated, and
exists only for the HEV path; Xray TUN mode does not expose it.

### 8.5 Preserve the original destination before NetLoop

NetLoop peer routing is keyed by the destination address presented by SOCKS.
Therefore NetLoop mode disables Xray sniffing on every retained inbound:

```text
sniffing.enabled = false
```

Do not inherit normal v2rayNG `destOverride`, `routeOnly`, FakeDNS sniffing, or
other destination-rewrite behavior. In particular, a connection to a ZeroTier
Managed IP must reach NetLoop with that original Managed IP even when TLS SNI,
HTTP Host, or QUIC metadata contains a domain name.

NetLoop mode also ignores the historical `PREF_VPN_DNS` value. The Android VPN
DNS server is fixed to `AppConfig.DNS_VPN` (`1.1.1.1`), and those DNS packets
still follow the normal NetLoop data path:

```text
TUN -> Xray -> NetLoop -> default exit -> 1.1.1.1:53
```

Do not add DNS fallback, probing, or remote-LAN DNS behavior.

The ordinary v2rayNG connection-delay test may still use the running Xray core
in NetLoop mode. However, skip the legacy `SpeedtestManager.getRemoteIPInfo()`
follow-up because NetLoop mode deliberately has no local HTTP inbound. Physical
device acceptance verifies the exit IP through normal captured application
traffic instead of reviving that HTTP proxy.

---

## 9. VPN recursion prevention

This is mandatory.

NetLoop's libzt physical sockets must not be captured by the v2rayNG VPN.

With two APKs:

```text
com.v2ray.ang       → outside VPN
com.libzt.netloop   → outside VPN
normal apps         → captured by VPN
```

Modify `CoreVpnService.configurePerAppProxy()` so the NetLoop package is always outside the VPN when NetLoop mode is enabled.

Behavior by mode:

### Global VPN mode

Add NetLoop to disallowed applications.

### Excluded-app mode

Ensure NetLoop is in the excluded/disallowed population.

### Allow-list mode

Ensure NetLoop is never added to the allowed population.

For NetLoop allow-list mode, every configured allowed package must exist.
`addAllowedApplication()` `NameNotFoundException` is fatal, and at least one
allowed application must be added successfully before the VPN is established.
Do not swallow a stale/uninstalled package and fall back to an empty allow-list,
because Android would then capture all applications including v2rayNG and
NetLoop themselves.

Do not call both Android `addAllowedApplication` and `addDisallowedApplication` on the same VPN builder.

This package exclusion is the primary recursion prevention mechanism for the two-APK design.

### 9.1 NetLoop mode captures private Managed IPs

NetLoop mode must not inherit v2rayNG's normal "Bypass LAN in VPN mode"
behavior. ZeroTier Managed IPs commonly live inside RFC1918 ranges such as
`172.16.0.0/12`; bypassing LAN at the Android VPN route layer would prevent
those destinations from ever reaching Xray or NetLoop.

When NetLoop mode is enabled, the VPN builder uses:

```text
VPN interface: configure both IPv4 and IPv6 TUN interface addresses
IPv4 route:    0.0.0.0/0
IPv6 route:    ::/0
```

and relies on per-app exclusion for `com.v2ray.ang` and
`com.libzt.netloop`. Do not dynamically derive ZeroTier CIDRs and do not
preserve the user's normal LAN-bypass preference or normal IPv6-enable preference
in NetLoop mode. NetLoop mode always captures both address families even when
the physical Wi-Fi/cellular network has no native IPv6 Internet connectivity.

---

## 10. Startup sequencing

The startup order is part of correctness.

Do not start Xray/VPN first and hope NetLoop becomes ready later.

Required order:

```text
1. User requests connect.
2. v2rayNG binds NetLoop control service.
3. v2rayNG GET_STATUS verifies the control API version.
4. v2rayNG always sends START(desired config); START either starts a stopped
   runtime, idempotently confirms the same READY/STARTING config, or rejects a
   different running config.
5. NetLoop starts libzt and local SOCKS.
6. NetLoop reports READY.
7. v2rayNG builds Xray config using localhost NetLoop SOCKS outbound.
8. v2rayNG establishes full-capture VPN/TUN routes and starts Xray.
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
stop/pause the current NetLoop-mode data path
    ↓
make one legal best-effort rebind/restart attempt
    ↓
if Android permits the restart:
    run the canonical GET_STATUS/version/state flow from section 4
    then surface START_SUCCESS
else:
    surface START_FAILURE
    remain stopped until the user presses Connect again
```

Do not send `MSG_STATE_START_FAILURE` merely because the one recovery attempt
has started. While recovery is in progress, use log/foreground-notification
diagnostics only; START_FAILURE is reserved for the final failed outcome.

The v2rayNG service-control broadcast receiver is owned by the Android Service
lifecycle, not by the Xray core loop. `CoreVpnService`, `CoreProxyOnlyService`,
and `CoreRootService` register it in `onCreate()` and unregister it in
`onDestroy()`. Therefore STOP is available while NetLoop is STARTING, while
Xray is running, and during the single NetLoop recovery attempt. A settings
screen return that consumed a restart-required change always sends STOP without
checking the UI process's `isRunning` flag; if no service exists the broadcast
is simply ignored.

NetLoop recovery keeps the existing `CoreVpnService` foreground identity and
the service-control receiver alive. Recovery cleanup stops Xray, HEV/TUN, and
the old VPN fd, but it does not call `stopForeground()` and does not unregister
the service receiver. Only final failure, explicit user STOP, or actual Service
destruction removes the foreground notification.

Do not silently continue sending Xray traffic into a dead localhost SOCKS port.
Do not add WorkManager, AlarmManager, exact alarms, watchdog services, special
permissions, or other background-restart infrastructure for this case. Android
background foreground-service restrictions mean an automatic cross-app restart
cannot be treated as guaranteed.

This personal fork has no automatic fixed-delay service restart. A settings
change while any runtime is active stops the current session and leaves it
stopped; the user presses Connect again. Selecting a legacy v2rayNG server while
NetLoop mode is active only updates the saved selection and does not touch the
running NetLoop session. In normal mode, changing the selected server may stop
the current session but still does not auto-start it.

The main-menu Restart command, notification Restart action, `MSG_STATE_RESTART`,
and all `stop -> 500 ms -> start` service-control paths are removed. User-driven
restart is simply Stop followed by a later Connect.

Explicit STOP has completion semantics. The service awaits
`coreController.stopLoop()`, completes service-specific teardown (VPN fd,
NetLoop companion, or root routing), then reports `MSG_STATE_STOP_SUCCESS` from
the terminating Service lifecycle. No fixed sleep is used to approximate Xray
shutdown. The core shutdown callback is suppressed during an explicit awaited
stop so a late callback from the old session cannot stop a newly created
Service instance.

### v2rayNG process/service restart

When VPN service restarts and NetLoop mode is enabled:

```text
bind NetLoop
    ↓
run the canonical GET_STATUS/version/state flow from section 4
```

NetLoop controlled mode itself is not sticky and has no persisted controlled
configuration. Recovery is always initiated from v2rayNG with a fresh
START(full desired config).

When `CoreVpnService.onDestroy()` is observably called outside the normal stop
path, it sends STOP to NetLoop rather than merely detaching the Binder. If the
entire v2rayNG process is killed and `onDestroy()` is never delivered, a
temporarily orphaned NetLoop process is accepted; do not add lease/heartbeat or
watchdog infrastructure for that case.

The personal v2rayNG fork does not ship the upstream 2dust release updater.
`CheckUpdateActivity`, `UpdateCheckerManager`, the pre-release update setting,
and the drawer entry are removed. Personal builds are updated manually from the
fork's own artifacts instead of attempting to install an upstream APK with a
different feature/signing lineage.

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

Caller filtering is mandatory. Hard-code the expected personal v2rayNG package,
`com.v2ray.ang`, in the NetLoop control service. For every Messenger command,
read the Messenger `Message.sendingUid`, resolve packages for that UID with
`PackageManager`, and process the command only if the expected package is
present. Reject all other callers. Do not defer this check to a Handler-time
`Binder.getCallingUid()` lookup because the Handler may no longer be executing
inside the original incoming Binder transaction.

If the personal v2rayNG fork uses another fixed application ID, change this one
source constant together with the fork. Do not add certificate infrastructure,
configurable ACLs, OAuth, or a general authorization framework.

The personal v2rayNG fork has no product flavors and uses only the fixed
`com.v2ray.ang` application ID. Do not add alternate package IDs to the NetLoop
caller allowlist. Android release output is arm64-v8a only.

---

## 14. Repository changes

### NetLoop repository

Expected primary files:

```text
src-csharp/NetLoop.Android/NetLoopControlService.cs
src-csharp/NetLoop.Android/NetLoopRuntimeController.cs
src-csharp/NetLoop.Android/NetLoopService.cs
src-csharp/NetLoop.Android/AndroidRuntimePaths.cs
src-csharp/NetLoop.Android/CiAutomation.cs   # NETLOOP_CI only
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
- in-memory controlled runtime config only
- hard-coded v2rayNG caller UID/package check

Do not modify SOCKS/libzt routing behavior.

### Phase 2 - v2rayNG control integration

Implement:

- NetLoopPluginManager
- install detection
- bind/rebind
- GET_STATUS version/state check immediately after bind/rebind
- START desired config after every successful version check, including READY and
  STARTING, so existing START idempotency validates configuration consistency
- start/wait READY
- stop
- status presentation

At the end of this phase, v2rayNG can fully operate NetLoop without opening the NetLoop app.

### Phase 3 - Automatic Xray outbound

When NetLoop mode is enabled:

- replace the runtime Internet outbound with SOCKS `127.0.0.1:1080`
- build a fresh minimal NetLoop-mode routing configuration instead of merging
  the active profile's routing rules
- route all captured TCP and UDP traffic to that NetLoop SOCKS outbound
- route intercepted DNS through the same NetLoop path so name resolution also exits through the configured default exit
- discard every inherited routing rule and outbound, including block/private-IP/
  direct/process rules, alternate proxy outbounds, balancers, regional rules,
  and the selected remote server profile
- disable sniffing on every retained inbound so Xray cannot replace Managed IP
  destinations with SNI/Host/QUIC domain names before SOCKS
- use only the fixed Android VPN DNS server `AppConfig.DNS_VPN`; do not read
  historical `PREF_VPN_DNS` in NetLoop mode
- prevent Xray start until NetLoop is READY
- force VPN route capture of both `0.0.0.0/0` and `::/0`, configure both
  IPv4 and IPv6 VPN interface addresses, and ignore both normal LAN-bypass and
  normal IPv6-enable preferences
- support both existing TUN engines: Xray TUN and HEV; the engine preference
  changes only the VPN-to-Xray ingress path, never the final NetLoop egress

In v1, Xray owns capture and protocol conversion, while NetLoop exclusively owns
peer routing and final egress selection.

Implement NetLoop mode as an early, dedicated runtime-config assembly branch.
Do not first build the normal profile routing/DNS configuration and then try to
filter rules out afterward. The NetLoop branch should construct only the
localhost NetLoop SOCKS outbound, the minimal routing needed to send captured
TCP/UDP to it, and DNS plumbing whose traffic also uses that path.

### Phase 4 - VPN exclusion correctness

Update per-app VPN handling so `com.libzt.netloop` is never captured by the v2rayNG VPN while NetLoop mode is active.

This phase is mandatory before considering the integration usable.

In the current upstream v2rayNG layout, the NetLoop branch belongs in
`CoreVpnService.configureNetworkSettings()`: it must bypass the normal
`routingRulesetsBypassLan()` and `PREF_IPV6_ENABLED` decisions and configure
both address families explicitly.

### Phase 5 - UI cleanup

Completed as a breaking cleanup:

- v2rayNG is the only normal user UI
- production NetLoop has no launcher Activity
- `AndroidConfig`, `netloop-config.json`, upstream SOCKS UI, Save & Start, and
  standalone START/sticky recovery are deleted
- NetLoop foreground notification is informational only and does not open a
  mutable standalone configuration screen
- Android emulator automation uses only the `NETLOOP_CI` broadcast receiver
- no migration/compatibility fallback exists for removed standalone mode

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

- Android compile/package for plain `release`; application ID must remain
  `com.v2ray.ang`
- release artifacts are `arm64-v8a` only; do not build 32-bit, x86/x86_64, or
  universal APKs for this personal NetLoop pairing
- assert NetLoop-mode VPN builder output always has IPv4 + IPv6 interface
  addresses and both `0.0.0.0/0` + `::/0` routes, independent of the normal
  LAN-bypass and IPv6 preferences
- inspect/assert the generated NetLoop-mode Xray JSON, not only the Kotlin
  builder code: captured TCP/UDP and DNS must resolve to the NetLoop SOCKS
  path, with exactly one proxy outbound, exactly one TCP/UDP catch-all, and no
  inherited block/direct/process rule, alternate proxy outbound, balancer,
  regional routing, or domestic-DNS direct egress
- assert every retained NetLoop inbound has sniffing disabled
- validate both NetLoop ingress variants at config/build level: Xray TUN must
  contain one tun inbound and no HEV-facing SOCKS inbound; HEV must contain the
  fixed loopback `127.0.0.1:10808` SOCKS inbound and no Xray tun inbound

Do not require Android emulator E2E as part of routine CI.

The existing `NETLOOP_CI` broadcast START hook is dormant test automation, not
a production launch surface. On Android 12+ a background broadcast receiver is
not a generally valid origin for `startForegroundService()`, so do not treat
that hook as a supported modern-emulator startup contract. If emulator E2E is
revived later, replace the START hop with a `NETLOOP_CI`-only foreground
Activity (or another platform-permitted test entry point) before relying on the
harness. Do not add WorkManager/watchdog infrastructure just to preserve the
old broadcast-start behavior.

### Manual physical-device acceptance

The useful final validation is on a real Android device.

Minimum acceptance cases:

1. NetLoop not installed -> v2rayNG gives a clear error.
2. NetLoop installed but stopped -> one Connect action starts both components.
3. Xray TUN mode: NetLoop READY -> Xray tun inbound -> SOCKS outbound
   `127.0.0.1:1080`.
4. HEV mode: NetLoop READY -> HEV -> Xray internal SOCKS
   `127.0.0.1:10808` -> SOCKS outbound `127.0.0.1:1080`.
5. Both Xray TUN and HEV capture IPv4 + IPv6 regardless of the normal IPv6
   preference.
6. TCP public IP is remote exit B in both TUN engines.
7. UDP public IP is remote exit B in both TUN engines.
8. DNS leak check shows DNS queries also leave through the NetLoop/exit-B path,
   use fixed `AppConfig.DNS_VPN`, and do not inherit an old `PREF_VPN_DNS` LAN
   resolver.
9. Connect directly to a peer Managed IP over HTTPS and QUIC with a hostname/SNI
   that differs from the IP; the SOCKS destination must remain the Managed IP
   and NetLoop must choose that peer rather than `default_exit`.
10. Wi-Fi -> cellular switch:
   - NetLoop sees the physical network change.
   - old NetLoop sessions are reset.
   - new TCP/UDP sessions recover.
   - no VPN recursion occurs.
11. cellular -> Wi-Fi switch behaves the same.
12. Stop in v2rayNG stops Xray/VPN and NetLoop.
13. Restart v2rayNG service while NetLoop is already READY -> v2rayNG rebinds and continues correctly.
14. Cold-start both apps, then press Connect in the visible v2rayNG main UI.
15. Try Quick Tile cold start and boot auto-start. If Android rejects the
    cross-app foreground-service start, keep those entry points unsupported for
    NetLoop mode rather than adding watchdog/WorkManager/AlarmManager machinery.
16. Kill NetLoop process while v2rayNG is active -> v2rayNG detects disconnect,
    stops/pauses NetLoop mode, surfaces a disconnected/failed state, and makes at most one legal
    best-effort restart attempt. If Android rejects the background FGS start,
    recovery waits for the next user Connect action.

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
    → captured TCP / UDP / DNS
    → SOCKS5 127.0.0.1:1080
    → NetLoop
    → self / explicit peer / required default exit
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
- optional direct peer Managed IPs are explicitly configured only when needed; the configured default exit is not duplicated in that list.
- `default_exit` is required on every node, including the exit node itself.
- captured TCP, UDP, and DNS all use the NetLoop path and configured ZeroTier exit B.
- Network switching does not require manually reopening either app.
- Both projects can still be built and upgraded independently.
