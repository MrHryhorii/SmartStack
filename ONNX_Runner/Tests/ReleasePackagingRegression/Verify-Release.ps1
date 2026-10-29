#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$ProjectDirectory = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$project = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$archives = @(Get-ChildItem -LiteralPath $run -Filter '*.zip' -File)
$sources = @(Get-ChildItem -LiteralPath $run -Filter 'Tsubaki-Corresponding-Sources-*.tar.gz' -File)
if ($archives.Count -eq 0 -or $sources.Count -ne 1) { throw 'Expected client ZIPs and one matching separate source archive.' }
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('Tsubaki-PackagingCheck-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$checks = 0
function Assert-FileHash {
    param([string]$File, [string]$Expected)
    if ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Expected.ToLowerInvariant()) {
        throw "File hash differs: $File"
    }
}
try {
    & tar -xzf $sources[0].FullName -C $work
    if ($LASTEXITCODE -ne 0) { throw 'Source archive could not be extracted.' }
    $manifest = Get-Content (Join-Path $work 'SOURCE_MANIFEST.json') -Raw | ConvertFrom-Json
    foreach ($entry in @($manifest.Archives) + @($manifest.NpmPackages) + @($manifest.SupplementalPackages) + @($manifest.SignalsmithSources) + @($manifest.AudioLibrarySources)) {
        Assert-FileHash (Join-Path $work $entry.File) $entry.Sha256
        $checks++
    }
    Assert-FileHash (Join-Path $work 'SOURCE_REVISIONS.txt') $manifest.SourceRevisionsSha256
    $checks++
    $sourceProject = Join-Path $work 'PROJECT_SOURCE/ONNX_Runner'
    foreach ($relative in @('ONNX_Runner.csproj', 'tools/Publish-WithLicenses.ps1', 'tools/Prepare-CorrespondingSources.ps1',
        'tools/NOTICE_SOURCES.json', 'VOICE_PROVENANCE.txt', 'THIRD_PARTY_LICENSES/SignalsmithStretch/PROVENANCE.txt',
        'THIRD_PARTY_LICENSES/DirectML/README.txt', 'Cloner/README.txt', 'readMe.md')) {
        Assert-FileHash (Join-Path $sourceProject $relative) (Get-FileHash (Join-Path $project $relative) -Algorithm SHA256).Hash
        $checks++
    }
    foreach ($line in (Get-Content (Join-Path $sourceProject 'FILES_SHA256.txt'))) {
        Assert-FileHash (Join-Path $sourceProject $line.Substring(66)) $line.Substring(0, 64)
        $checks++
    }
    $legalFiles = @(Get-ChildItem (Join-Path $project 'THIRD_PARTY_LICENSES') -Recurse -File |
        Where-Object { $_.Name -notin @('README.txt', 'PROVENANCE.txt') } |
        ForEach-Object { [System.IO.Path]::GetRelativePath($project, $_.FullName).Replace('\', '/') })
    $legalFiles += 'LICENSE', 'PHOIBLE/LICENSE.txt'
    foreach ($archive in $archives) {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.FullName)
        try {
            $prefix = $archive.BaseName + '/'
            $entries = @{}
            foreach ($entry in $zip.Entries) {
                if (-not $entry.FullName.StartsWith($prefix, [System.StringComparison]::Ordinal) -or $entry.FullName.Contains('\')) {
                    throw "Invalid client ZIP path: $($entry.FullName)"
                }
                $relative = $entry.FullName.Substring($prefix.Length)
                if ($relative -match '(^|/)(PROVENANCE\.txt|VOICE_PROVENANCE\.txt|MODEL_PROVENANCE\.txt|PACKAGE_INFO\.txt|SOURCE_MANIFEST\.json|SOURCE_ACCESS\.txt|RELEASE_CONTENTS\.txt|NUGET_PUBLISH_INVENTORY\.txt|appsettings\.Development\.json|readMe\.md|README\.txt)$|\.(nuspec|pdb|cs|csproj|ps1)$') {
                    throw "Review/source file leaked into client ZIP: $relative"
                }
                if (-not $entry.FullName.EndsWith('/')) { $entries[$relative] = $entry }
                $checks++
            }
            foreach ($relative in $legalFiles + @('THIRD_PARTY_LICENSES/Piper-Model-NOTICES.txt')) {
                $entry = $entries[$relative]
                if (-not $entry -or $entry.Length -eq 0) { throw "Missing or empty client notice: $relative" }
                $original = if ($relative -eq 'THIRD_PARTY_LICENSES/Piper-Model-NOTICES.txt') { 'Model/MODEL_PROVENANCE.txt' } else { $relative }
                $stream = $entry.Open()
                try { $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) }
                finally { $stream.Dispose() }
                if ($hash -ne (Get-FileHash (Join-Path $project $original) -Algorithm SHA256).Hash) { throw "Client notice changed: $relative" }
                $checks++
            }
            $notice = [System.IO.StreamReader]::new($entries['THIRD_PARTY_NOTICES.txt'].Open())
            try { $noticeText = $notice.ReadToEnd() } finally { $notice.Dispose() }
            if (-not $noticeText.Contains($sources[0].Name)) { throw 'Client notices do not identify the matching source archive.' }
            if ($noticeText.Contains('Available as a separate download alongside this binary')) { throw 'Client notices promise unverified publication.' }
            $checks++
            $variant = switch ($archive.BaseName) {
                'tsubaki-tts-engine-windows-x64-cpu' { 'Windows-CPU' }
                'tsubaki-tts-engine-windows-x64-directml' { 'Windows-DML' }
                'tsubaki-tts-engine-windows-x64-webgpu' { 'Windows-WebGPU' }
                'tsubaki-tts-engine-linux-x64-cpu' { 'Linux-CPU' }
                'tsubaki-tts-engine-linux-x64-cuda' { 'Linux-CUDA' }
                'tsubaki-tts-engine-linux-x64-webgpu' { 'Linux-WebGPU' }
                default { throw "Unknown client archive: $($archive.Name)" }
            }
            $review = Join-Path $work "BUILD_REVIEW/$variant"
            foreach ($relative in @('project.assets.json', 'NUGET_PUBLISH_INVENTORY.txt', 'PackageMetadata', 'PUBLISHED_FILES_SHA256.txt')) {
                if (-not (Test-Path -LiteralPath (Join-Path $review $relative))) { throw "Missing build evidence: $variant/$relative" }
                $checks++
            }
            $recordCount = 0
            foreach ($line in (Get-Content (Join-Path $review 'PUBLISHED_FILES_SHA256.txt'))) {
                $relative = $line.Substring(66)
                $entry = $entries[$relative]
                if (-not $entry) { throw "Inventory file absent from client ZIP: $relative" }
                $stream = $entry.Open()
                try { $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) }
                finally { $stream.Dispose() }
                if ($hash.ToLowerInvariant() -ne $line.Substring(0, 64)) { throw "Archived client file changed: $relative" }
                $recordCount++
                $checks++
            }
            if ($recordCount -ne $entries.Count) { throw 'Build evidence does not cover every client file.' }
            $checks++
        }
        finally { $zip.Dispose() }
    }
    "Release packaging: $checks/$checks checks passed for $($archives.Count) client archive(s)."
}
finally { Remove-Item -LiteralPath $work -Recurse -Force }
