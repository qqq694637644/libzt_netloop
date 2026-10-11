# Manual, offline NetLoop application upgrade / rollback. No service,
# watchdog, scheduled task or process management is installed by this tool.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Apply', 'Rollback')]
    [string] $Action,
    [Parameter(Mandatory = $true)]
    [string] $InstallDirectory,
    [Parameter(Mandatory = $true)]
    [string] $BackupRoot,
    [string[]] $Packages = @(),
    [string] $Snapshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function FullPath([string] $path) {
    return [IO.Path]::GetFullPath($path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function IsUnder([string] $candidate, [string] $parent) {
    return $candidate.StartsWith(
        $parent + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Assert-FileName([string] $name) {
    if ([string]::IsNullOrWhiteSpace($name) -or
        $name -ne [IO.Path]::GetFileName($name) -or
        $name -notmatch '^[A-Za-z0-9_.+-]+\.(exe|dll|json|pdb|dat|config)$' -or
        $name -match '^(identity|state|peers|authtoken)\.' -or
        $name -match '\.(log|jsonl|secret|bak)$') {
        throw "Unsafe or unsupported application package filename: $name"
    }
}

$install = FullPath $InstallDirectory
$backups = FullPath $BackupRoot
if ((IsUnder $backups $install) -or (IsUnder $install $backups) -or
    $backups -eq $install -or
    $install -eq (FullPath ([IO.Path]::GetPathRoot($install))) -or
    $backups -eq (FullPath ([IO.Path]::GetPathRoot($backups)))) {
    throw 'InstallDirectory and BackupRoot must be separate, non-root, non-nested directories.'
}
if (-not (Test-Path -LiteralPath $install -PathType Container)) {
    throw "Application directory not found: $install"
}

# Never stop an application or run a service on behalf of the user.
if (@(Get-Process -Name 'netloop' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Close all netloop.exe processes manually before upgrading or rolling back.'
}

if (-not (Test-Path -LiteralPath $backups)) {
    New-Item -ItemType Directory -Path $backups -Force | Out-Null
}

function Replace-File([string] $source, [string] $destination) {
    $temp = $destination + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $replacedBackup = $temp + '.previous'
    try {
        [IO.File]::Copy($source, $temp)
        if ([IO.File]::Exists($destination)) {
            # .NET on Windows rejects a null Replace backup path; use an
            # explicit temporary path and remove it after atomic replacement.
            [IO.File]::Replace($temp, $destination, $replacedBackup)
        } else {
            [IO.File]::Move($temp, $destination)
        }
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
        if ([IO.File]::Exists($replacedBackup)) {
            [IO.File]::Delete($replacedBackup)
        }
    }
}

if ($Action -eq 'Apply') {
    if ($Packages.Count -eq 0) {
        throw 'Apply requires -Packages with one or more trusted release ZIP archives.'
    }

    $staging = Join-Path ([IO.Path]::GetDirectoryName($install)) (
        'netloop-package-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $snapshotPath = Join-Path $backups (
        ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')) + '-' +
        [guid]::NewGuid().ToString('N'))
    $previous = Join-Path $snapshotPath 'previous'
    $changes = @()

    try {
        $uniqueFiles = New-Object 'System.Collections.Generic.HashSet[string]' (
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($zipPath in $Packages) {
            $fullZip = FullPath $zipPath
            if (-not [IO.File]::Exists($fullZip)) {
                throw "Package not found: $fullZip"
            }
            $archive = [IO.Compression.ZipFile]::OpenRead($fullZip)
            try {
                foreach ($entry in $archive.Entries) {
                    if ($entry.Name.Length -eq 0) {
                        throw "Package contains a directory: $($entry.FullName)"
                    }
                    if ($entry.FullName -cne $entry.Name) {
                        throw "Only flat release packages are supported: $($entry.FullName)"
                    }
                    Assert-FileName $entry.Name
                    if (-not $uniqueFiles.Add($entry.Name)) {
                        throw "Duplicate package file: $($entry.Name)"
                    }
                    $path = Join-Path $staging $entry.Name
                    $inputStream = $entry.Open()
                    try {
                        $outputStream = [IO.File]::Create($path)
                        try { $inputStream.CopyTo($outputStream) }
                        finally { $outputStream.Dispose() }
                    }
                    finally { $inputStream.Dispose() }
                }
            }
            finally { $archive.Dispose() }
        }

        foreach ($required in @('netloop.exe', 'libzt.dll')) {
            if (-not $uniqueFiles.Contains($required)) {
                throw "Release package set is missing $required"
            }
        }

        New-Item -ItemType Directory -Path $previous -Force | Out-Null
        foreach ($file in (Get-ChildItem -LiteralPath $staging -File | Sort-Object Name)) {
            $destination = Join-Path $install $file.Name
            $existed = [IO.File]::Exists($destination)
            if ($existed) {
                [IO.File]::Copy($destination, (Join-Path $previous $file.Name))
            }
            $changes += [pscustomobject]@{
                name = $file.Name
                existed = $existed
                new_sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                previous_sha256 = if ($existed) {
                    (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
                } else { $null }
            }
        }
        $manifest = [pscustomobject]@{
            schema = 1
            created_utc = [DateTimeOffset]::UtcNow.ToString('O')
            state = 'staged'
            files = $changes
        }
        $manifestFile = Join-Path $snapshotPath 'manifest.json'
        $manifest | ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath $manifestFile -Encoding UTF8

        try {
            foreach ($entry in $changes) {
                Replace-File (Join-Path $staging $entry.name) (Join-Path $install $entry.name)
            }
            $manifest.state = 'applied'
            $manifest | ConvertTo-Json -Depth 6 |
                Set-Content -LiteralPath $manifestFile -Encoding UTF8
        }
        catch {
            foreach ($entry in $changes) {
                $destination = Join-Path $install $entry.name
                if ($entry.existed) {
                    Replace-File (Join-Path $previous $entry.name) $destination
                } elseif ([IO.File]::Exists($destination)) {
                    [IO.File]::Delete($destination)
                }
            }
            $manifest.state = 'failed_restored'
            $manifest | ConvertTo-Json -Depth 6 |
                Set-Content -LiteralPath $manifestFile -Encoding UTF8
            throw
        }
        Write-Output "Manual upgrade applied. Rollback snapshot: $snapshotPath"
        Write-Output 'NetLoop has NOT been started or restarted.'
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($Snapshot)) {
        throw 'Rollback requires -Snapshot pointing to a previous Apply snapshot directory.'
    }
    $snapshotPath = FullPath $Snapshot
    if (-not (IsUnder $snapshotPath $backups)) {
        throw 'Rollback snapshot must be inside BackupRoot.'
    }
    $manifestFile = Join-Path $snapshotPath 'manifest.json'
    if (-not [IO.File]::Exists($manifestFile)) {
        throw "Rollback manifest not found: $manifestFile"
    }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.state -ne 'applied') {
        throw 'Rollback requires a completed Apply snapshot.'
    }
    foreach ($entry in $manifest.files) {
        Assert-FileName $entry.name
        $destination = Join-Path $install $entry.name
        if (-not [IO.File]::Exists($destination)) {
            throw "Installed application file missing, refusing partial rollback: $destination"
        }
        $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($actual -ne $entry.new_sha256) {
            throw "Application file was modified after Apply; refusing rollback: $destination"
        }
        if ($entry.existed) {
            $previousFile = Join-Path (Join-Path $snapshotPath 'previous') $entry.name
            if (-not [IO.File]::Exists($previousFile) -or
                (Get-FileHash -LiteralPath $previousFile -Algorithm SHA256).Hash -ne
                    $entry.previous_sha256) {
                throw "Snapshot is incomplete or damaged: $previousFile"
            }
        }
    }
    foreach ($entry in $manifest.files) {
        $destination = Join-Path $install $entry.name
        if ($entry.existed) {
            Replace-File (Join-Path (Join-Path $snapshotPath 'previous') $entry.name) $destination
        } else {
            [IO.File]::Delete($destination)
        }
    }
    $manifest.state = 'rolled_back'
    $manifest | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $manifestFile -Encoding UTF8
    Write-Output "Manual rollback completed: $snapshotPath"
    Write-Output 'NetLoop has NOT been started or restarted.'
}
