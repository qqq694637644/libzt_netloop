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
