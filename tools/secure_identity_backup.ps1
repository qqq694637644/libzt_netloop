# Manual, offline DPAPI CURRENT_USER backup/restore of the ZeroTier node
# identity. No watchdog, service, scheduled task, or process restart.
# DPAPI backups require the same Windows user/profile and installation.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Backup', 'Restore')]
    [string] $Action,
    [Parameter(Mandatory = $true)]
    [string] $StateDirectory,
    [Parameter(Mandatory = $true)]
    [string] $BackupFile,
    [switch] $AllowOverwrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'DPAPI CurrentUser backup is supported only on Windows.'
}
Add-Type -AssemblyName System.Security

function Hash-Bytes([byte[]] $bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Lock-Down([string] $path) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $path '/inheritance:r' '/grant:r' ('*' + $sid + ':(F)') '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' |
        Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enforce owner-only/SYSTEM/Administrators ACL on: $path"
    }
}

function Save-SecureFile([string] $path, [byte[]] $bytes) {
    $temp = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $replacedBackup = $temp + '.previous'
    try {
        # Create an empty file, restrict its ACL, THEN write any secrets.
        $stream = [IO.File]::Open(
            $temp, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Dispose()
        Lock-Down $temp
        [IO.File]::WriteAllBytes($temp, $bytes)
        if ([IO.File]::Exists($path)) {
            Lock-Down $path
            [IO.File]::Replace($temp, $path, $replacedBackup)
        } else {
            [IO.File]::Move($temp, $path)
        }
        Lock-Down $path
    }
    finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
        if ([IO.File]::Exists($replacedBackup)) {
            [IO.File]::Delete($replacedBackup)
        }
    }
}

$state = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\', '/')
$backup = [IO.Path]::GetFullPath($BackupFile)
if ($backup.StartsWith(
        $state + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Encrypted backup must be outside the ZeroTier state directory.'
}
if (@(Get-Process -Name 'netloop' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close all netloop.exe processes manually before backing up/restoring identity.'
}

$identityNames = @('identity.secret', 'identity.public')
if ($Action -eq 'Backup') {
    if ([IO.File]::Exists($backup)) {
        throw 'Refusing to overwrite an existing encrypted identity backup.'
    }
    $payloadFiles = @()
    foreach ($name in $identityNames) {
        $path = Join-Path $state $name
        if (-not [IO.File]::Exists($path)) {
            throw "Required ZeroTier identity file is absent: $name"
        }
        $content = [IO.File]::ReadAllBytes($path)
        $payloadFiles += [pscustomobject]@{
            name = $name
            sha256 = Hash-Bytes $content
            content = [Convert]::ToBase64String($content)
        }
    }

    $payload = [pscustomobject]@{
        schema = 1
        type = 'netloop-identity-dpapi-currentuser'
        created_utc = [DateTimeOffset]::UtcNow.ToString('O')
        files = $payloadFiles
    }
    $plaintext = [Text.Encoding]::UTF8.GetBytes(
        ($payload | ConvertTo-Json -Depth 5 -Compress))
    $protected = [Security.Cryptography.ProtectedData]::Protect(
        $plaintext, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)

    $parent = [IO.Path]::GetDirectoryName($backup)
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    Save-SecureFile $backup $protected
    Write-Output 'Encrypted identity backup created (Windows DPAPI CurrentUser).'
    Write-Output 'This backup requires the original Windows user/profile and machine.'
}
else {
    if (-not [IO.File]::Exists($backup)) {
        throw 'Encrypted backup file not found.'
    }
    $ciphertext = [IO.File]::ReadAllBytes($backup)
    $plaintext = [Security.Cryptography.ProtectedData]::Unprotect(
        $ciphertext, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $payload = [Text.Encoding]::UTF8.GetString($plaintext) | ConvertFrom-Json
    if ($payload.schema -ne 1 -or
        $payload.type -ne 'netloop-identity-dpapi-currentuser' -or
        @($payload.files).Count -ne 2) {
        throw 'Encrypted backup has an invalid identity archive format.'
    }

    $validated = @{}
    foreach ($entry in $payload.files) {
        if ($entry.name -notin $identityNames -or $validated.ContainsKey($entry.name)) {
            throw 'Encrypted backup contains an unexpected or duplicate identity filename.'
        }
        $content = [Convert]::FromBase64String($entry.content)
        if ((Hash-Bytes $content) -ne $entry.sha256) {
            throw 'Encrypted identity archive failed its integrity check.'
        }
        $validated.Add($entry.name, $content)
    }
    foreach ($name in $identityNames) {
        if (-not $validated.ContainsKey($name)) {
            throw "Encrypted archive is missing $name"
        }
        $destination = Join-Path $state $name
        if ([IO.File]::Exists($destination) -and -not $AllowOverwrite) {
            throw 'Existing identity found; use -AllowOverwrite only after backing up the current identity.'
        }
    }

    [IO.Directory]::CreateDirectory($state) | Out-Null
    foreach ($name in $identityNames) {
        Save-SecureFile (Join-Path $state $name) $validated[$name]
    }
    Write-Output 'Identity restored with restricted Windows file ACLs.'
    Write-Output 'No NetLoop/ZeroTier process was started or restarted.'
}
