param(
    [string]$Configuration = 'Release',
    [string]$WindowsFFmpegArchive = '',
    [string]$LinuxFFmpegArchive = '',
    [string]$FFmpegChecksumsFile = '',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'STT_Runner.csproj'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$buildRoot = Join-Path $projectRoot ("Builds/{0}-{1}" -f $stamp, [guid]::NewGuid().ToString('N').Substring(0, 8))
$sources = Join-Path $buildRoot 'Sources'
$extract = Join-Path $buildRoot '_extract'

# Pin both architectures to the same FFmpeg source commit and LGPL build variant.
$release = 'autobuild-2026-09-27-13-04'
$revision = 'n9.0.2-12-gc867e13549'
$releaseBase = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$release"
$variants = @(
    @{
        Rid = 'win-x64'
        Archive = $WindowsFFmpegArchive
        ArchiveName = "ffmpeg-$revision-win64-lgpl-9.0.zip"
        BinaryName = 'ffmpeg.exe'
    },
    @{
        Rid = 'linux-x64'
        Archive = $LinuxFFmpegArchive
        ArchiveName = "ffmpeg-$revision-linux64-lgpl-9.0.tar.xz"
        BinaryName = 'ffmpeg'
    }
)

function Get-FFmpegArchive($variant) {
    if ($variant.Archive) {
        $path = (Resolve-Path -LiteralPath $variant.Archive).Path
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "FFmpeg archive is not a file: $path" }
        return $path
    }

    $destination = Join-Path $sources $variant.ArchiveName
    $url = "$releaseBase/$($variant.ArchiveName)"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $destination -UseBasicParsing
    return $destination
}

function Get-FFmpegChecksums {
    if ($FFmpegChecksumsFile) {
        $path = (Resolve-Path -LiteralPath $FFmpegChecksumsFile).Path
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "FFmpeg checksums file is not a file: $path" }
    }
    else {
        $path = Join-Path $sources 'checksums.sha256'
        Invoke-WebRequest -Uri "$releaseBase/checksums.sha256" -OutFile $path -UseBasicParsing
    }

    $hashes = @{}
    foreach ($line in Get-Content -LiteralPath $path) {
        if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$') { continue }
        $name = [System.IO.Path]::GetFileName($Matches[2].Trim().Replace('\', '/'))
        if ($hashes.ContainsKey($name)) { throw "Duplicate FFmpeg checksum for $name" }
        $hashes[$name] = $Matches[1]
    }
    foreach ($variant in $variants) {
        if (!$hashes.ContainsKey($variant.ArchiveName)) {
            throw "The FFmpeg checksum manifest does not contain $($variant.ArchiveName)"
        }
    }
    return $hashes
}

function Copy-FFmpegFromZip($archivePath, $publishPath, $binaryName) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $binary = @($archive.Entries | Where-Object {
            $_.FullName.Replace('\', '/') -like "*/bin/$binaryName"
        })
        if ($binary.Count -ne 1) { throw "Expected one $binaryName in $archivePath; found $($binary.Count)." }

        $destination = Join-Path $publishPath $binaryName
        $sourceStream = $binary[0].Open()
        $targetStream = [System.IO.File]::Create($destination)
        try { $sourceStream.CopyTo($targetStream) }
        finally { $targetStream.Dispose(); $sourceStream.Dispose() }

        $legal = Join-Path $publishPath 'Licenses/FFmpeg-build'
        New-Item -ItemType Directory -Path $legal -Force | Out-Null
        foreach ($entry in $archive.Entries) {
            $name = [System.IO.Path]::GetFileName($entry.FullName.Replace('\', '/'))
            if ($name -notmatch '^(LICENSE|COPYING|README)(\.|$)') { continue }
            $entryStream = $entry.Open()
            $fileStream = [System.IO.File]::Create((Join-Path $legal $name))
            try { $entryStream.CopyTo($fileStream) }
            finally { $fileStream.Dispose(); $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Copy-FFmpegFromTar($archivePath, $publishPath, $binaryName, $rid) {
    $destination = Join-Path $extract $rid
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    & tar -xJf $archivePath -C $destination
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $archivePath" }

    $binaries = @(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter $binaryName |
        Where-Object { $_.FullName.Replace('\', '/') -like "*/bin/$binaryName" })
    if ($binaries.Count -ne 1) { throw "Expected one $binaryName in $archivePath; found $($binaries.Count)." }
    Copy-Item -LiteralPath $binaries[0].FullName -Destination (Join-Path $publishPath $binaryName)

    $legal = Join-Path $publishPath 'Licenses/FFmpeg-build'
    New-Item -ItemType Directory -Path $legal -Force | Out-Null
    Get-ChildItem -LiteralPath $destination -Recurse -File |
        Where-Object { $_.Name -match '^(LICENSE|COPYING|README)(\.|$)' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $legal $_.Name) -Force }
}

function Set-UnixZipPermissions($zipPath) {
    # .NET marks ZIPs created on Windows as DOS archives. Mark Linux entries as
    # Unix so extractors apply the executable bits stored in ExternalAttributes.
    $stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
    $reader = New-Object System.IO.BinaryReader($stream)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $scan = [Math]::Min($stream.Length, 65557)
        $stream.Position = $stream.Length - $scan
        $tail = $reader.ReadBytes([int]$scan)
        $eocd = -1
        for ($i = $tail.Length - 22; $i -ge 0; $i--) {
            if ([BitConverter]::ToUInt32($tail, $i) -eq [uint32]0x06054b50) { $eocd = $i; break }
        }
        if ($eocd -lt 0) { throw "Missing ZIP central directory: $zipPath" }
        $count = [BitConverter]::ToUInt16($tail, $eocd + 10)
        $offset = [BitConverter]::ToUInt32($tail, $eocd + 16)
        if ($count -eq [uint16]::MaxValue -or $offset -eq [uint32]::MaxValue) {
            throw "ZIP64 is not supported by this release script: $zipPath"
        }

        $stream.Position = $offset
        for ($i = 0; $i -lt $count; $i++) {
            $entry = $stream.Position
            if ($reader.ReadUInt32() -ne [uint32]0x02014b50) { throw "Invalid ZIP central entry: $zipPath" }
            $stream.Position = $entry + 28
            $nameLength = $reader.ReadUInt16()
            $extraLength = $reader.ReadUInt16()
            $commentLength = $reader.ReadUInt16()
            $stream.Position = $entry + 46
            $name = [System.Text.Encoding]::UTF8.GetString($reader.ReadBytes($nameLength))
            $mode = 420 # 0644
            if ($name -eq 'MwandishiSTT/MwandishiSTT' -or $name -eq 'MwandishiSTT/ffmpeg') {
                $mode = 493 # 0755
            }
            $stream.Position = $entry + 5
            $writer.Write([byte]3) # Version made by: Unix.
            $stream.Position = $entry + 38
            $writer.Write([uint32](([long](32768 + $mode)) * 65536))
            $stream.Position = $entry + 46 + $nameLength + $extraLength + $commentLength
        }
    }
    finally { $writer.Dispose(); $reader.Dispose(); $stream.Dispose() }
}

function Write-ReleaseZip($publishPath, $zipPath, $rid) {
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $prefix = $publishPath.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
        foreach ($file in Get-ChildItem -LiteralPath $publishPath -Recurse -File) {
            $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
            $entryName = "MwandishiSTT/$relative"
            if ($entryName.Contains('\')) { throw "Invalid ZIP entry: $entryName" }
            $entry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
    if ($rid -eq 'linux-x64') { Set-UnixZipPermissions $zipPath }
}

if (!(Test-Path -LiteralPath $projectFile -PathType Leaf)) { throw "Project not found: $projectFile" }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$selfContained = 'true'
$useAppHost = 'true'
if ($FrameworkDependent) { $selfContained = 'false'; $useAppHost = 'false' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $sources -Force | Out-Null
$expectedHashes = Get-FFmpegChecksums

foreach ($variant in $variants) {
    $rid = $variant.Rid
    $publishPath = Join-Path $buildRoot $rid
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
    Write-Host "Publishing $rid"
    & $dotnet publish $projectFile --configuration $Configuration --runtime $rid --self-contained $selfContained "/p:UseAppHost=$useAppHost" --output $publishPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    $archivePath = Get-FFmpegArchive $variant
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($archiveHash -ne $expectedHashes[$variant.ArchiveName]) {
        throw "FFmpeg archive SHA-256 mismatch for $($variant.ArchiveName): expected $($expectedHashes[$variant.ArchiveName]), got $archiveHash"
    }
    Write-Host "Verified SHA-256 for $($variant.ArchiveName)"
    if ($rid -eq 'win-x64') {
        Copy-FFmpegFromZip $archivePath $publishPath $variant.BinaryName
    }
    else {
        Copy-FFmpegFromTar $archivePath $publishPath $variant.BinaryName $rid
    }

    $binary = Join-Path $publishPath $variant.BinaryName
    if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { throw "FFmpeg is missing from $publishPath" }
    $hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    $upstream = "$releaseBase/$($variant.ArchiveName)"
    $provenance = @(
        "Binary: $($variant.BinaryName)",
        "Source archive: $upstream",
        "Source archive SHA-256: $archiveHash",
        "Packaged binary SHA-256: $hash",
        'Variant: LGPL, static, x64',
        'FFmpeg source revision: c867e13549',
        'FFmpeg source: https://github.com/FFmpeg/FFmpeg/commit/c867e13549',
        "Build recipe: https://github.com/BtbN/FFmpeg-Builds/releases/tag/$release",
        'License: https://ffmpeg.org/legal.html'
    )
    $provenance | Set-Content -LiteralPath (Join-Path $publishPath 'Licenses/FFmpeg-build/PROVENANCE.txt') -Encoding UTF8

    $zipPath = Join-Path $buildRoot "MwandishiSTT-$rid.zip"
    Write-ReleaseZip $publishPath $zipPath $rid
    $verify = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($verify.Entries | ForEach-Object { $_.FullName })
        if ($names | Where-Object { $_.Contains('\') -or !$_.StartsWith('MwandishiSTT/') }) {
            throw "The ZIP has invalid directory separators: $zipPath"
        }
        foreach ($required in @("MwandishiSTT/$($variant.BinaryName)", 'MwandishiSTT/THIRD-PARTY-NOTICES.md', 'MwandishiSTT/Assets/warmup.wav', 'MwandishiSTT/wwwroot/index.html')) {
            if ($names -notcontains $required) { throw "Missing ZIP entry: $required" }
        }
        $native = 'MwandishiSTT/runtimes/vulkan/win-x64/whisper.dll'
        if ($rid -eq 'linux-x64') { $native = 'MwandishiSTT/runtimes/vulkan/linux-x64/libwhisper.so' }
        if ($names -notcontains $native) { throw "Missing native runtime: $native" }
        if ($rid -eq 'linux-x64') {
            foreach ($name in @('MwandishiSTT/ffmpeg', 'MwandishiSTT/MwandishiSTT')) {
                $entry = $verify.GetEntry($name)
                if ($null -eq $entry -and $FrameworkDependent) { continue }
                if ($null -eq $entry -or (($entry.ExternalAttributes -shr 16) -band 511) -ne 493) {
                    throw "Linux executable bit is missing: $name"
                }
            }
        }
    }
    finally { $verify.Dispose() }
    Write-Host "Created $zipPath"
}

if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
Write-Host "Releases are in $buildRoot"
