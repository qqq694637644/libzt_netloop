# A / C 到 B 的独立连通性探测

本工具用于诊断「ZeroTier 显示在线或离线，但 A/C 通过 B 出口访问是否真的可用」。
它不会重启 NetLoop，不会修改 ZeroTier 身份和路由，也不会触发 NetLoop 的运行时重置。
从 A 或 C 的本地 SOCKS5 `127.0.0.1:1080` 发起全新的请求，访问
B 的 ZeroTier IP `172.26.0.254:8080`。请先在 B 确认 8080 本机服务可用。

## 使用步骤

1. **不要先删除旧日志。** 保留 A/C 的 `client.log`、B 的 `server.log`，
   以及各节点原有的 `state` 目录（尤其是 `identity.secret`）。
   如果 BAT 启动时会覆盖旧日志，请在重启之前先复制旧日志到其他目录。
2. NetLoop 和 B 的 8080 服务保持运行。把
   `tools/connection_probe.ps1` 复制到 A/C 的运行目录，或在仓库目录直接运行。
3. 在 A 的 PowerShell 窗口运行：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\connection_probe.ps1 -Node A
   ```

   在 C 的 PowerShell 窗口运行：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\connection_probe.ps1 -Node C
   ```

   如果从仓库根目录运行，将文件路径改为
   `.\tools\connection_probe.ps1`。使用单独的 PowerShell 窗口，
   这样关闭监测不会影响 NetLoop。
4. 监测过程中可以正常切换 A 的 Wi-Fi/手机热点，或记录 B 的断网时间。
   请**不要为复现故障而随意同时重启 A 和 B**。使用 `Ctrl+C` 停止监测。
5. 交付分析时提供相同 UTC 时间段的 A/C 探测 JSONL、对应
   `client.log` 及 B 的 `server.log`。

## 参数与日志格式

默认每次最多等待 12 秒，两次请求之间间隔 20 秒；无限运行直到 Ctrl+C。
默认自动在当前目录的 `evidence` 文件夹创建新文件：
`connection-probe-A-20261011T013000000Z.jsonl`（文件名仅为示例）。
**不会覆盖已有日志**。若使用 `-LogPath`，则向指定文件追加。

需要更细的切换网络观测时，使用：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\connection_probe.ps1 `
  -Node A -IntervalSeconds 2 -TimeoutSeconds 5
```

只测三次、显式设置代理和输出路径：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\connection_probe.ps1 `
  -Node C -Count 3 -SocksHost 127.0.0.1 -SocksPort 1080 `
  -LogPath .\evidence\C-manual-probe.jsonl
```

每次探测写入一行 JSON，主要字段如下：

| 字段 | 含义 |
| --- | --- |
| `ts_utc` / `end_utc` | 开始与结束 UTC 时间，便于对齐 NetLoop 的 `ts` |
| `node` / `sequence` | A/C 标签与本次监测中的序号 |
| `status` | `success` 或 `failure` |
| `curl_exit_code` | curl 返回码；常见 `97` 表示 SOCKS5 连接失败，`28` 表示超时 |
| `http_code` | HTTP 响应码；`000` 说明没获得 HTTP 响应 |
| `elapsed_ms` / `curl_total_seconds` | 外层探测耗时与 curl 报告的请求耗时 |
| `error` | curl 错误文字，成功则为 `null` |

`HTTP 404` / `500` 仍算作「代理链路能够收到 HTTP 响应」，
不代表目标服务业务正常；真正失败看 `status=failure`、
`curl_exit_code` 和 `error`。这个工具测试**新建 TCP/SOCKS5 请求**，
并不能证明既有长连接有没有断开。

筛选失败记录：

```powershell
Get-Content .\evidence\connection-probe-A-*.jsonl |
  ForEach-Object { $_ | ConvertFrom-Json } |
  Where-Object { $_.status -eq 'failure' } |
  Select-Object ts_utc, curl_exit_code, elapsed_ms, error
```

20 秒的探测间隔适合长期观察，但无法严格证明秒级恢复。
需要测量秒级恢复时可临时用 `-IntervalSeconds 1` 或 `2`，
结束后恢复较低频率，避免探测本身制造高并发。

## TCP 建连与 Agent accept 日志关联

PR #2 增加了**诊断日志**，不改变现有 750ms/3s 超时、ZeroTier 路径
管理和断网重置机制。A、B 两端都更新后，针对每个新建连接，可区分：

| 事件 | 节点 | 表示什么 |
| --- | --- | --- |
| `tcp_connect_start` | A/C | 发起一次新的 libzt TCP socket 尝试 |
| `tcp_connect_completed` | A/C | libzt 报告 TCP 建连完成；**尚不代表 SOCKS5 握手成功** |
| `tcp_connect_timeout` | A/C | socket 被外部取消，或 libzt 建连期限耗尽；以 `reason` 区分 |
| `tcp_connect_failed` | A/C | socket 创建/配置或连接时出现明确异常 |
| `socks_peer_handshake_completed` | A/C | 已连接 B Agent 且 SOCKS5 greeting 成功 |
| `socks_peer_handshake_failed` | A/C | TCP 已连接，但 SOCKS5 greeting 失败 |
| `tcp_accept_completed` | B | libzt `accept()` 成功返回，**早于 SOCKS5 greeting 和 CONNECT 解析** |
| `tcp_accept_setup_failed` / `tcp_accept_rejected` | B | 已 accept，但初始化失败或连接数达到上限 |
| `socks_connect_request` | B | 读到了完整的 SOCKS5 CONNECT 目标 |
| `overlay_socks_client_failed` | B | accept 后 SOCKS5 greeting/请求处理异常 |

**关联标识规则：** `connection_id` 是当前进程内收到的本地 SOCKS5
连接 ID，A 的同一个 ID 贯穿 `socks_connect_request`、
`socks_peer_negotiation_retry`、`tcp_connect_*` 和 `socks_connect_failed`。
`peer_attempt_id` 标识一次 750ms SOCKS5 peer negotiation；
`connect_id` 是 libzt connector 的一次调用；
`attempt_id` 唯一标识这次调用里实际创建的原生 socket，
避免因 fd 被复用而误判。B 的 `tcp_accept_completed` 和随后
`socks_connect_request` 具有**相同的 B 本地 `connection_id`**。

**A 的 ID 与 B 的 ID 并不相同**，因为我们没有修改 SOCKS5 网络协议，
这些 ID 不经网络传输。可以用 A 的 `tcp_connect_completed` /
`tcp_connect_timeout` 中的 `local`
（例如 `172.26.0.2:53152`）去对应 B
`tcp_accept_completed` 中的 `remote`；还需确认目标
`172.26.0.254:42042`、UTC 时间窗与端口是否可能被复用。不能仅凭
源端口跳号声称 B 收到了 TCP SYN。

假设再次观察到 3 秒失败，先在 A 上提取这次失败的连接 ID：

```powershell
$events = Get-Content .\A_client.log | ForEach-Object { $_ | ConvertFrom-Json }
$failure = $events | Where-Object {
  $_.event -eq 'socks_connect_failed' -and
  $_.data.target -eq '172.26.0.254:8080'
} | Select-Object -Last 1

$events | Where-Object {
  $_.data.connection_id -eq $failure.data.connection_id
} | Select-Object ts, event, data
```

然后在 B 上观察该时间窗的接入、解析和握手失败情况：

```powershell
Get-Content .\server.log |
  ForEach-Object { $_ | ConvertFrom-Json } |
  Where-Object {
    $_.ts -ge '2026-10-11T01:53:15' -and
    $_.ts -lt '2026-10-11T01:53:45' -and
    $_.event -in @(
      'tcp_accept_completed', 'tcp_accept_setup_failed',
      'tcp_accept_rejected', 'socks_connect_request',
      'overlay_socks_client_failed')
  } | Select-Object ts, event, data
```

上面时间窗只是历史示例，请改成**实际故障的 UTC 时间**。
`socket_error_source=abandoned_socket_snapshot` 表示读取 SO_ERROR 时已经
决定放弃该 fd；返回 `socket_error=0` 不代表握手成功。
如果 B 有 `tcp_accept_completed` 但没有相同 `connection_id` 的
`socks_connect_request`，则应重点查看
`overlay_socks_client_failed` 或 `tcp_accept_rejected`。
若 B 没有 accept 日志，只能证明该 TCP 连接未进入 B 的 Agent 处理，
不能推断底层 UDP 包有没有到达 B。
