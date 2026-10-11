# Windows PowerShell 5.1 / PowerShell 7.
# Independent SOCKS5-over-NetLoop probe; does not change the NetLoop runtime.
[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9_-]{1,32}$')]
    [string]$Node = 'client',

    [ValidatePattern('^https?://')]
    [string]$TargetUrl = 'http://172.26.0.254:8080/',

    [ValidateNotNullOrEmpty()]
    [string]$SocksHost = '127.0.0.1',

    [ValidateRange(1, 65535)]
    [int]$SocksPort = 1080,

    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 12,

    [ValidateRange(1, 3600)]
    [int]$IntervalSeconds = 20,

    # Zero means run until Ctrl+C.
    [ValidateRange(0, 2147483647)]
    [int]$Count = 0,

    # When omitted, create a new timestamped file, never overwrite old logs.
    [string]$LogPath,

    # Also allows an offline mock command for checking the script.
    [ValidateNotNullOrEmpty()]
    [string]$CurlCommand = 'curl.exe'
)

$ErrorActionPreference = 'Stop'
$null = Get-Command -Name $CurlCommand -ErrorAction Stop

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $evidenceDirectory = Join-Path -Path (Get-Location).Path -ChildPath 'evidence'
    $LogPath = Join-Path -Path $evidenceDirectory -ChildPath (
        'connection-probe-{0}-{1}.jsonl' -f $Node, $stamp)
}
$LogPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
$directory = [IO.Path]::GetDirectoryName($LogPath)
[void][IO.Directory]::CreateDirectory($directory)
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$proxy = '{0}:{1}' -f $SocksHost, $SocksPort

# With Windows PowerShell 5.1, --noproxy "" may drop its empty argument.
# Suppress NO_PROXY in *this probe process* instead, so every request really
# goes through the configured SOCKS5 proxy. -q ignores .curlrc bypass rules.
$originalNoProxy = [Environment]::GetEnvironmentVariable('NO_PROXY', 'Process')
$originalNoProxyLower = [Environment]::GetEnvironmentVariable('no_proxy', 'Process')
[Environment]::SetEnvironmentVariable('NO_PROXY', $null, 'Process')
[Environment]::SetEnvironmentVariable('no_proxy', $null, 'Process')

Write-Host "Probe $Node -> $TargetUrl via SOCKS5 $proxy"
Write-Host "Writing JSONL to $LogPath (interval: ${IntervalSeconds}s, timeout: ${TimeoutSeconds}s). Ctrl+C to stop."

try {
    $sequence = 0
    while ($Count -eq 0 -or $sequence -lt $Count) {
        $sequence++
        $startedAt = [DateTimeOffset]::UtcNow
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $response = @()
        $exitCode = -1

        try {
            # Do not treat a nonzero curl exit code as a terminating PowerShell
            # error. Failed probes are first-class JSONL samples.
            $ErrorActionPreference = 'Continue'
            $response = @(& $CurlCommand -q --silent --show-error `
                --max-time $TimeoutSeconds `
                --socks5-hostname $proxy `
                --output NUL `
                --write-out 'NETLOOP_HTTP=%{http_code} NETLOOP_TOTAL=%{time_total}' `
                $TargetUrl 2>&1)
            $exitCode = $LASTEXITCODE
        }
        catch {
            $response = @($_.Exception.Message)
            $exitCode = -1
        }
        finally {
            $ErrorActionPreference = 'Stop'
            $watch.Stop()
        }

        $output = (@($response) | ForEach-Object { [string]$_ }) -join "`n"
        $marker = [regex]::Match(
            $output,
            'NETLOOP_HTTP=(?<http>[0-9]{3}) NETLOOP_TOTAL=(?<total>[0-9]+(?:\.[0-9]+)?)')
        $httpCode = $null
        $curlSeconds = $null
        if ($marker.Success) {
            $httpCode = $marker.Groups['http'].Value
            $curlSeconds = [double]::Parse(
                $marker.Groups['total'].Value,
                [Globalization.CultureInfo]::InvariantCulture)
        }

        # Any HTTP response (including 404/500) proves SOCKS5 connectivity.
        # HTTP 000 or a nonzero curl exit code means the probe failed.
        $success = $exitCode -eq 0 -and $marker.Success -and $httpCode -ne '000'
        $errorText = $null
        if (-not $success) {
            $errorText = [regex]::Replace(
                $output,
                'NETLOOP_HTTP=[0-9]{3} NETLOOP_TOTAL=[0-9]+(?:\.[0-9]+)?',
                '').Trim()
            if ([string]::IsNullOrWhiteSpace($errorText)) {
                $errorText = 'No HTTP response returned by the SOCKS5 request.'
            }
            if ($errorText.Length -gt 1000) {
                $errorText = $errorText.Substring(0, 1000)
            }
        }

        $sample = [ordered]@{
            ts_utc = $startedAt.ToString('o')
            end_utc = [DateTimeOffset]::UtcNow.ToString('o')
            event = 'connection_probe'
            node = $Node
            sequence = $sequence
            target = $TargetUrl
            socks_proxy = $proxy
            status = $(if ($success) { 'success' } else { 'failure' })
            curl_exit_code = $exitCode
            http_code = $httpCode
            elapsed_ms = [math]::Round($watch.Elapsed.TotalMilliseconds, 1)
            curl_total_seconds = $curlSeconds
            error = $errorText
        }

        $line = ConvertTo-Json -InputObject $sample -Depth 4 -Compress
        [IO.File]::AppendAllText(
            $LogPath, $line + [Environment]::NewLine, $utf8NoBom)
        Write-Host (
            '{0} node={1} {2} exit={3} http={4} elapsed_ms={5}' -f
            $sample.ts_utc, $Node, $sample.status, $exitCode,
            $httpCode, $sample.elapsed_ms)

        if ($Count -ne 0 -and $sequence -ge $Count) {
            break
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        'NO_PROXY', $originalNoProxy, 'Process')
    [Environment]::SetEnvironmentVariable(
        'no_proxy', $originalNoProxyLower, 'Process')
}
