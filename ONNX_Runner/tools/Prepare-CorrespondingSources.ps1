param(
    [string]$ProjectDirectory,
    [string]$SourcesRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if (-not $ProjectDirectory) { $ProjectDirectory = Join-Path $PSScriptRoot '..' }
$projectDirectory = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$cacheRoot = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA }
    elseif ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME }
    else { Join-Path $HOME '.cache' }
if (-not $SourcesRoot) { $SourcesRoot = Join-Path $cacheRoot 'Tsubaki/CorrespondingSources' }
$sourcesRoot = [System.IO.Path]::GetFullPath($SourcesRoot)

foreach ($command in @('git', 'tar')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "$command is required to collect the corresponding source."
    }
}

$workRoot = Join-Path $cacheRoot 'Tsubaki/SourceCache'
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$completed = $false

function Assert-Hash([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing file: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "Unexpected SHA-256 for $Path`: $actual" }
}

function Invoke-Git([string[]]$Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed: $($Arguments -join ' ')" }
}

function Save-Download([string]$Url, [string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return }
    $partial = "$Path.partial"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $partial
        Move-Item -LiteralPath $partial -Destination $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    }
}

function Assert-Revision([string]$Directory, [string]$Expected) {
    $actual = (& git -C $Directory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Expected) {
        throw "Cached source revision mismatch: $Directory. Remove $workRoot to start over."
    }
}

$manifestFile = Join-Path $sourcesRoot 'SOURCE_MANIFEST.json'
if ((Test-Path -LiteralPath $manifestFile -PathType Leaf) -and -not $Force) {
    $existing = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($existing.EspeakDllSha256 -ne '9588480f8197df62fd8461a8431f8eaec6e8e7749c5ffcbe7fee656fe40a2189' -or
        $existing.Mpg123BundleSha256 -ne 'c13a80f0455795eb0c8a1c7844fb5e567cf37acc00fdd3e1f3e6b89f7b4601ce' -or
        $existing.OggOpusBundleSha256 -ne 'c5055d3410ca02728d10708e154b47219c38ceef52155f18702e34204150651f') {
        throw "Source manifest does not match this project: $manifestFile"
    }
    $entries = @($existing.Archives) + @($existing.NpmPackages) + @($existing.SupplementalPackages)
    if (@($existing.Archives).Count -ne 4 -or @($existing.NpmPackages).Count -ne 2 -or
        @($existing.SupplementalPackages).Count -lt 1) {
        throw "Incomplete source manifest: $manifestFile"
    }
    foreach ($entry in $entries) {
        Assert-Hash (Join-Path $sourcesRoot $entry.File) $entry.Sha256
    }
    Assert-Hash (Join-Path $sourcesRoot 'SOURCE_REVISIONS.txt') $existing.SourceRevisionsSha256
    Write-Host "Reusing corresponding source at $sourcesRoot"
    return
}

try {
    $bundles = @(
        @{ Package = 'mpg123-decoder'; Version = '1.0.3'; File = 'mpg123-decoder.min.js'; Module = 'modules/mpg123'; Hash = 'c13a80f0455795eb0c8a1c7844fb5e567cf37acc00fdd3e1f3e6b89f7b4601ce' },
        @{ Package = 'ogg-opus-decoder'; Version = '1.7.5'; File = 'ogg-opus-decoder.min.js'; Module = 'modules/opus'; Hash = 'c5055d3410ca02728d10708e154b47219c38ceef52155f18702e34204150651f' }
    )
    foreach ($bundle in $bundles) {
        $local = Join-Path $projectDirectory (Join-Path 'wwwroot/js/codecs/lib' $bundle.File)
        Assert-Hash $local $bundle.Hash
        $tarball = Join-Path $workRoot "$($bundle.Package)-$($bundle.Version).tgz"
        $url = "https://registry.npmjs.org/$($bundle.Package)/-/$($bundle.Package)-$($bundle.Version).tgz"
        Save-Download $url $tarball
        $extracted = Join-Path $workRoot "$($bundle.Package)-package"
        New-Item -ItemType Directory -Path $extracted -Force | Out-Null
        & tar -xzf $tarball -C $extracted
        if ($LASTEXITCODE -ne 0) { throw "Could not unpack $tarball" }
        $matches = @(Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter $bundle.File)
        $matchingBytes = @($matches | Where-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq $bundle.Hash
        })
        if ($matchingBytes.Count -eq 0) {
            throw "No byte-identical $($bundle.File) in $($bundle.Package) $($bundle.Version). Review the actual bundle origin."
        }
    }

    $espeak = Join-Path $projectDirectory 'PiperNative/espeak-ng.dll'
    Assert-Hash $espeak '9588480f8197df62fd8461a8431f8eaec6e8e7749c5ffcbe7fee656fe40a2189'

    $espeakSource = Join-Path $workRoot 'espeak-ng-piper'
    if (-not (Test-Path -LiteralPath (Join-Path $espeakSource '.git'))) {
        if (Test-Path -LiteralPath $espeakSource) { Remove-Item -LiteralPath $espeakSource -Recurse -Force }
        Invoke-Git @('clone', 'https://github.com/rhasspy/espeak-ng.git', $espeakSource)
        Invoke-Git @('-C', $espeakSource, 'checkout', '--detach', '0f65aa301e0d6bae5e172cc74197d32a6182200f')
    }
    Assert-Revision $espeakSource '0f65aa301e0d6bae5e172cc74197d32a6182200f'
    Invoke-Git @('-C', $espeakSource, 'submodule', 'update', '--init', '--recursive')

    $phonemizeSource = Join-Path $workRoot 'piper-phonemize'
    if (-not (Test-Path -LiteralPath (Join-Path $phonemizeSource '.git'))) {
        if (Test-Path -LiteralPath $phonemizeSource) { Remove-Item -LiteralPath $phonemizeSource -Recurse -Force }
        Invoke-Git @('clone', '--depth', '1', '--branch', '2023.11.14-4', '--recurse-submodules', 'https://github.com/rhasspy/piper-phonemize.git', $phonemizeSource)
    }
    Assert-Revision $phonemizeSource 'fccd4f335aa68ac0b72600822f34d84363daa2bf'
    Invoke-Git @('-C', $phonemizeSource, 'submodule', 'update', '--init', '--recursive')

    $sourceNames = @('espeak-ng-piper', 'piper-phonemize')
    $codecParserVersions = New-Object System.Collections.Generic.List[string]
    foreach ($bundle in $bundles) {
        $name = "$($bundle.Package)-$($bundle.Version)"
        $sourceNames += $name
        $target = Join-Path $workRoot $name
        $tag = "$($bundle.Package)/$($bundle.Version)"
        if (-not (Test-Path -LiteralPath (Join-Path $target '.git'))) {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
            Invoke-Git @('clone', '--depth', '1', '--branch', $tag, 'https://github.com/eshaz/wasm-audio-decoders.git', $target)
        }
        $tagRevision = (& git -C $target rev-parse "$tag^{commit}").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Cached tag is missing: $tag in $target" }
        Assert-Revision $target $tagRevision
        Invoke-Git @('-C', $target, 'submodule', 'update', '--depth', '1', '--init', '--recursive', '--', $bundle.Module)
        $lockPath = Join-Path $target 'package-lock.json'
        if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) { throw "Missing decoder dependency lockfile: $lockPath" }
        $lockText = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8
        $parserVersion = [regex]::Match(
            $lockText,
            '"(?:node_modules/)?codec-parser"\s*:\s*\{[^{}]*"version"\s*:\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[^"]+)?)"')
        if (-not $parserVersion.Success) { throw "Cannot identify codec-parser source for $tag in $lockPath" }
        $version = $parserVersion.Groups['version'].Value
        if (-not $codecParserVersions.Contains($version)) { $codecParserVersions.Add($version) }
    }

    $revisions = New-Object System.Collections.Generic.List[string]
    foreach ($name in $sourceNames) {
        $directory = Join-Path $workRoot $name
        $head = (& git -C $directory rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve source revision for $name" }
        $revisions.Add("$name $head")
        $relatedBundle = @($bundles | Where-Object { "$($_.Package)-$($_.Version)" -eq $name })
        $module = if ($relatedBundle.Count -gt 0) { $relatedBundle[0].Module } else { $null }
        $submodules = if ($module) {
            @(& git -C $directory submodule status --recursive -- $module)
        }
        else {
            @(& git -C $directory submodule status --recursive)
        }
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect submodules for $name" }
        foreach ($line in $submodules) {
            if ($line -match '^[-+U]') { throw "Submodule is not at the recorded source revision: $name $line" }
            $revisions.Add("  $line")
        }
    }

    $stage = Join-Path $workRoot 'staged'
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $supplemental = New-Object System.Collections.Generic.List[string]
    foreach ($version in $codecParserVersions) {
        $file = "codec-parser-$version.tgz"
        $archive = Join-Path $stage $file
        $cachedArchive = Join-Path $workRoot $file
        Save-Download "https://registry.npmjs.org/codec-parser/-/$file" $cachedArchive
        Copy-Item -LiteralPath $cachedArchive -Destination $archive
        $extracted = Join-Path $workRoot "codec-parser-$version"
        New-Item -ItemType Directory -Path $extracted -Force | Out-Null
        & tar -xzf $archive -C $extracted
        if ($LASTEXITCODE -ne 0) { throw "Could not unpack codec-parser $version" }
        if (@(Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter '*.js').Count -eq 0) {
            throw "codec-parser $version package did not contain JavaScript source"
        }
        $supplemental.Add($file)
        $revisions.Add("codec-parser $version (npm package; version from decoder package-lock.json)")
    }
    $revisions | Set-Content -LiteralPath (Join-Path $stage 'SOURCE_REVISIONS.txt') -Encoding UTF8
    foreach ($name in $sourceNames) {
        $archive = Join-Path $stage "$name.tar.gz"
        & tar --exclude=.git -czf $archive -C $workRoot $name
        if ($LASTEXITCODE -ne 0) { throw "Could not archive $name" }
    }
    foreach ($bundle in $bundles) {
        Copy-Item -LiteralPath (Join-Path $workRoot "$($bundle.Package)-$($bundle.Version).tgz") -Destination $stage
    }

    $manifest = [ordered]@{
        Description = 'Source snapshots and matching npm packages for the bundled eSpeak and browser decoder binaries.'
        EspeakDllSha256 = '9588480f8197df62fd8461a8431f8eaec6e8e7749c5ffcbe7fee656fe40a2189'
        Mpg123BundleSha256 = $bundles[0].Hash
        OggOpusBundleSha256 = $bundles[1].Hash
        SourceRevisionsSha256 = (Get-FileHash -LiteralPath (Join-Path $stage 'SOURCE_REVISIONS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
        Archives = @($sourceNames | ForEach-Object {
            $file = "$_.tar.gz"
            [ordered]@{ File = $file; Sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        NpmPackages = @($bundles | ForEach-Object {
            $file = "$($_.Package)-$($_.Version).tgz"
            [ordered]@{ File = $file; Sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        SupplementalPackages = @($supplemental | ForEach-Object {
            [ordered]@{ File = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'SOURCE_MANIFEST.json') -Encoding UTF8
    if (Test-Path -LiteralPath $sourcesRoot) { Remove-Item -LiteralPath $sourcesRoot -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $sourcesRoot -Parent) -Force | Out-Null
    Move-Item -LiteralPath $stage -Destination $sourcesRoot
    $completed = $true
    Write-Host "Corresponding source staged at $sourcesRoot"
}
finally {
    if ($completed) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Warning "Source checkout retained for retry at $workRoot"
    }
}
