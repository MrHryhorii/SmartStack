# Publishes selected runtime/provider bundles with project, NuGet, and runtime-pack notices.
# References the pinned corresponding-source archive prepared by the companion script.
param(
    [ValidateSet('All', 'Windows-CPU', 'Windows-DML', 'Windows-WebGPU', 'Linux-CPU', 'Linux-CUDA', 'Linux-WebGPU')]
    [string]$Variant = 'All',
    [string]$ProjectDirectory,
    [string]$OutputRoot,
    [string]$ReviewRoot,
    [string]$SourcesRoot
)

$ErrorActionPreference = 'Stop'

function Assert-SignalsmithDistribution {
    param([string]$PublishDirectory, [string]$RuntimeIdentifier, [string]$PackageDirectory,
        [string]$PackageVersion = '1.1.2')

    if ($PackageVersion -ne '1.1.2') {
        throw "Signalsmith version changed; review its pinned sources and notices: $PackageVersion"
    }
    $nativeName = switch ($RuntimeIdentifier) {
        'win-x64' { 'SignalsmithStretch.dll' }
        'linux-x64' { 'libSignalsmithStretch.so' }
        default { throw "Signalsmith distribution has not been reviewed for: $RuntimeIdentifier" }
    }
    $nativeFile = Join-Path $PublishDirectory $nativeName
    $originalFile = Join-Path $PackageDirectory "runtimes/$RuntimeIdentifier/native/$nativeName"
    if (-not (Test-Path -LiteralPath $nativeFile -PathType Leaf)) {
        throw "Published build is missing Signalsmith native code: $nativeFile"
    }
    if (-not (Test-Path -LiteralPath $originalFile -PathType Leaf)) {
        throw "The restored Signalsmith package is missing its native asset: $originalFile"
    }
    $nativeHash = (Get-FileHash -LiteralPath $nativeFile -Algorithm SHA256).Hash
    if ($nativeHash -ne (Get-FileHash -LiteralPath $originalFile -Algorithm SHA256).Hash) {
        throw "Published Signalsmith native code differs from the restored package: $nativeFile"
    }
    foreach ($name in @('Wrapper-MIT-0.txt', 'Stretch-MIT.txt', 'Linear-MIT.txt')) {
        $notice = Join-Path $PublishDirectory "THIRD_PARTY_LICENSES/SignalsmithStretch/$name"
        if (-not (Test-Path -LiteralPath $notice -PathType Leaf)) {
            throw "Published build is missing a Signalsmith component notice: $notice"
        }
        if ((Get-Item -LiteralPath $notice).Length -eq 0) {
            throw "Published Signalsmith component notice is empty: $notice"
        }
    }
    return "Signalsmith native asset: $RuntimeIdentifier/$nativeName | SHA256 $nativeHash | wrapper/core/FFT notices included"
}

function Copy-ProjectSource {
    param([string]$ProjectDirectory, [string]$Destination, [string[]]$ExcludedDirectories)

    # Runtime weights and compiled binaries are not corresponding source. Keep data,
    # build inputs, original samples, notices and provenance with the project source.
    $root = $ProjectDirectory.TrimEnd([char[]]@('\', '/'))
    $excluded = @($ExcludedDirectories | ForEach-Object {
        [System.IO.Path]::GetFullPath($_).TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    })
    $hashes = New-Object System.Collections.Generic.List[string]
    foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -File -Force)) {
        if ($excluded | Where-Object { $file.FullName.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) }) { continue }
        $relative = $file.FullName.Substring($root.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|\.git|\.vs|\.vscode|Publish-Licensed)(/|$)' -or
            $relative -match '(^|/)\.env($|\.)' -or
            $relative -match '\.(onnx|ort|dll|so|dylib|exe|pdb|partial|zip|tar\.gz)$') { continue }
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashes.Add("$hash  $relative")
    }
    $hashes | Sort-Object | Set-Content -LiteralPath (Join-Path $Destination 'FILES_SHA256.txt') -Encoding UTF8
}
if (-not $ProjectDirectory) { $ProjectDirectory = Join-Path $PSScriptRoot '..' }
$projectDirectory = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$projectFile = Join-Path $projectDirectory 'ONNX_Runner.csproj'
if (-not $OutputRoot) { $OutputRoot = Join-Path $projectDirectory 'Publish-Licensed' }
if (-not $ReviewRoot) { $ReviewRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'Tsubaki-LicenseReview' }
if (-not $SourcesRoot) {
    $cacheRoot = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA }
        elseif ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME }
        else { Join-Path $HOME '.cache' }
    $SourcesRoot = Join-Path $cacheRoot 'Tsubaki/CorrespondingSources'
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$reviewRoot = [System.IO.Path]::GetFullPath($ReviewRoot)
$SourcesRoot = [System.IO.Path]::GetFullPath($SourcesRoot)
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $outputRoot $runId
$reviewRun = Join-Path $reviewRoot $runId
$variants = @(
    @{ Name = 'Windows-CPU'; Artifact = 'tsubaki-tts-engine-windows-x64-cpu'; Rid = 'win-x64'; Property = '-p:CpuOnly=true' },
    @{ Name = 'Windows-DML'; Artifact = 'tsubaki-tts-engine-windows-x64-directml'; Rid = 'win-x64'; Property = $null },
    @{ Name = 'Windows-WebGPU'; Artifact = 'tsubaki-tts-engine-windows-x64-webgpu'; Rid = 'win-x64'; Property = '-p:UseWebGpu=true' },
    @{ Name = 'Linux-CPU'; Artifact = 'tsubaki-tts-engine-linux-x64-cpu'; Rid = 'linux-x64'; Property = '-p:CpuOnly=true' },
    @{ Name = 'Linux-CUDA'; Artifact = 'tsubaki-tts-engine-linux-x64-cuda'; Rid = 'linux-x64'; Property = $null },
    @{ Name = 'Linux-WebGPU'; Artifact = 'tsubaki-tts-engine-linux-x64-webgpu'; Rid = 'linux-x64'; Property = '-p:UseWebGpu=true' }
)

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is required.'
}

$noticeSources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'NOTICE_SOURCES.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($notice in $noticeSources.Notices) {
    $file = Join-Path $projectDirectory $notice.File
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $notice.Sha256) {
        throw "Upstream notice changed; review its source record: $($notice.File)"
    }
}
$requiredNotices = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'THIRD_PARTY_LICENSES') -Recurse -File |
    Where-Object { $_.Name -notin @('README.txt', 'PROVENANCE.txt') } |
    ForEach-Object { $_.FullName.Substring($projectDirectory.TrimEnd([char[]]@('\', '/')).Length).TrimStart([char[]]@('\', '/')) })
$requiredNotices += 'LICENSE'

$modelDirectory = Join-Path $projectDirectory 'Model'
$models = @(Get-ChildItem -LiteralPath $modelDirectory -Filter '*.onnx' -File -ErrorAction SilentlyContinue)
if ($models.Count -eq 0) {
    throw "A ready-to-run release needs a Piper .onnx model in $modelDirectory. Restore the model from your complete project before publishing."
}
foreach ($model in $models) {
    $officialConfig = "$($model.FullName).json"
    $simpleConfig = Join-Path $model.DirectoryName ($model.BaseName + '.json')
    if (-not (Test-Path -LiteralPath $officialConfig -PathType Leaf) -and
        -not (Test-Path -LiteralPath $simpleConfig -PathType Leaf)) {
        throw "Missing Piper model configuration for: $($model.FullName)"
    }
}
$phoibleFile = Join-Path $projectDirectory 'PHOIBLE/phoible.csv'
$phoibleLicense = Join-Path $projectDirectory 'PHOIBLE/LICENSE.txt'
if (-not (Test-Path -LiteralPath $phoibleFile -PathType Leaf)) {
    throw "A ready-to-run release needs the PHOIBLE dataset at $phoibleFile. Restore it from your complete project before publishing."
}
if (-not (Test-Path -LiteralPath $phoibleLicense -PathType Leaf)) {
    throw "A ready-to-run release needs the PHOIBLE license notice at $phoibleLicense."
}

$appSettingsPath = Join-Path $projectDirectory 'appsettings.json'
$appSettings = Get-Content -LiteralPath $appSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$cloningEnabled = $true
if ($null -ne $appSettings.ClonerSettings -and $null -ne $appSettings.ClonerSettings.EnableCloning) {
    $cloningEnabled = [bool]$appSettings.ClonerSettings.EnableCloning
}
$requiredClonerFiles = @('tone_extract.onnx', 'tone_color.onnx', 'tone_config.json')
if ($cloningEnabled) {
    $clonerDirectory = Join-Path $projectDirectory 'Cloner'
    foreach ($fileName in $requiredClonerFiles) {
        $filePath = Join-Path $clonerDirectory $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Cloning is enabled, so the ready-to-run offline release needs $filePath. Restore the OpenVoice model files before publishing."
        }
    }
}

$manifestFile = Join-Path $SourcesRoot 'SOURCE_MANIFEST.json'
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
    $prepareScript = Join-Path $PSScriptRoot 'Prepare-CorrespondingSources.ps1'
    if (-not (Test-Path -LiteralPath $prepareScript -PathType Leaf)) {
        throw "Missing source preparation helper: $prepareScript"
    }
    Write-Host 'Preparing corresponding source once for the bundled native/browser binaries.'
    & $prepareScript -ProjectDirectory $projectDirectory -SourcesRoot $SourcesRoot
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
        throw "Source preparation did not create $manifestFile"
    }
}
$sourceManifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
# Older caches are extended in place; verified existing source archives are reused.
if (@($sourceManifest.SignalsmithSources).Count -ne 3 -or @($sourceManifest.AudioLibrarySources).Count -ne 3) {
    & (Join-Path $PSScriptRoot 'Prepare-CorrespondingSources.ps1') `
        -ProjectDirectory $projectDirectory -SourcesRoot $SourcesRoot
    $sourceManifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($sourceManifest.SignalsmithSources).Count -ne 3 -or @($sourceManifest.AudioLibrarySources).Count -ne 3) {
        throw 'Corresponding source manifest is missing Signalsmith or audio-library source snapshots.'
    }
}
if ($sourceManifest.EspeakDllSha256 -ne '9588480f8197df62fd8461a8431f8eaec6e8e7749c5ffcbe7fee656fe40a2189' -or
    $sourceManifest.Mpg123BundleSha256 -ne 'c13a80f0455795eb0c8a1c7844fb5e567cf37acc00fdd3e1f3e6b89f7b4601ce' -or
    $sourceManifest.OggOpusBundleSha256 -ne 'c5055d3410ca02728d10708e154b47219c38ceef52155f18702e34204150651f') {
    throw 'Bundled binary hashes differ from the reviewed project versions; review and update the source preparation script.'
}
$bundleHashes = @(
    @{ File = 'mpg123-decoder.min.js'; Hash = $sourceManifest.Mpg123BundleSha256 },
    @{ File = 'ogg-opus-decoder.min.js'; Hash = $sourceManifest.OggOpusBundleSha256 }
)
foreach ($bundle in $bundleHashes) {
    $file = Join-Path $projectDirectory (Join-Path 'wwwroot/js/codecs/lib' $bundle.File)
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $bundle.Hash) { throw "Browser decoder changed; regenerate corresponding source: $file" }
}
$espeak = Join-Path $projectDirectory 'PiperNative/espeak-ng.dll'
$espeakHash = (Get-FileHash -LiteralPath $espeak -Algorithm SHA256).Hash.ToLowerInvariant()
if ($espeakHash -ne $sourceManifest.EspeakDllSha256) {
    throw 'Bundled eSpeak DLL changed; regenerate and review corresponding source.'
}
$sourceFiles = @($sourceManifest.Archives) + @($sourceManifest.NpmPackages) + @($sourceManifest.SupplementalPackages) + @($sourceManifest.SignalsmithSources) + @($sourceManifest.AudioLibrarySources) + @([pscustomobject]@{
    File = 'SOURCE_REVISIONS.txt'; Sha256 = $sourceManifest.SourceRevisionsSha256
})
if (@($sourceManifest.Archives).Count -ne 4 -or @($sourceManifest.NpmPackages).Count -ne 2 -or
    @($sourceManifest.SupplementalPackages).Count -lt 1) {
    throw 'Corresponding source manifest is incomplete: expected four source archives, two decoder packages, and codec-parser source.'
}
foreach ($entry in $sourceFiles) {
    $source = Join-Path $SourcesRoot $entry.File
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing corresponding source: $source" }
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Sha256) { throw "Corresponding source hash mismatch: $source" }
}

$sourcePackageName = 'Tsubaki-Corresponding-Sources-{0}-{1}-{2}-{3}.tar.gz' -f $runId, `
    $sourceManifest.EspeakDllSha256.Substring(0, 12), `
    $sourceManifest.Mpg123BundleSha256.Substring(0, 12), `
    $sourceManifest.OggOpusBundleSha256.Substring(0, 12)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$sourcePackagePath = Join-Path $runRoot $sourcePackageName
Copy-ProjectSource -ProjectDirectory $projectDirectory `
    -Destination (Join-Path $reviewRun 'PROJECT_SOURCE/ONNX_Runner') `
    -ExcludedDirectories @($outputRoot, $reviewRoot, $SourcesRoot)

foreach ($build in $variants) {
    if ($Variant -ne 'All' -and $Variant -ne $build.Name) { continue }

    $publishDirectory = Join-Path $runRoot $build.Artifact
    $archivePath = Join-Path $runRoot "$($build.Artifact).zip"
    $buildReview = Join-Path $reviewRun "BUILD_REVIEW/$($build.Name)"
    New-Item -ItemType Directory -Path $buildReview -Force | Out-Null
    if ((Test-Path -LiteralPath $publishDirectory) -or (Test-Path -LiteralPath $archivePath)) {
        throw "Release output already exists: $($build.Artifact). Choose another -OutputRoot or start a new run."
    }
    $arguments = @('publish', $projectFile, '-c', 'Release', '-r', $build.Rid,
        '--self-contained', 'true', '--force', '-o', $publishDirectory)
    if ($build.Property) { $arguments += $build.Property }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($build.Name)." }

    foreach ($relative in $requiredNotices) {
        $original = Join-Path $projectDirectory $relative
        $published = Join-Path $publishDirectory $relative
        if (-not (Test-Path -LiteralPath $published -PathType Leaf) -or
            (Get-Item -LiteralPath $published).Length -eq 0 -or
            (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash) {
            throw "Published license or notice is missing, empty or changed: $relative"
        }
    }

    foreach ($model in $models) {
        $relativeModel = Join-Path 'Model' $model.Name
        $publishedModel = Join-Path $publishDirectory $relativeModel
        if (-not (Test-Path -LiteralPath $publishedModel -PathType Leaf)) {
            throw "Published build is missing Piper model: $publishedModel"
        }
        $publishedOfficialConfig = "$publishedModel.json"
        $publishedSimpleConfig = Join-Path (Split-Path $publishedModel -Parent) ($model.BaseName + '.json')
        if (-not (Test-Path -LiteralPath $publishedOfficialConfig -PathType Leaf) -and
            -not (Test-Path -LiteralPath $publishedSimpleConfig -PathType Leaf)) {
            throw "Published build is missing Piper model configuration for: $publishedModel"
        }
    }
    $publishedPhoible = Join-Path $publishDirectory 'PHOIBLE/phoible.csv'
    if (-not (Test-Path -LiteralPath $publishedPhoible -PathType Leaf)) {
        throw "Published build is missing PHOIBLE data: $publishedPhoible"
    }
    $publishedPhoibleLicense = Join-Path $publishDirectory 'PHOIBLE/LICENSE.txt'
    if (-not (Test-Path -LiteralPath $publishedPhoibleLicense -PathType Leaf)) {
        throw "Published build is missing PHOIBLE license notice: $publishedPhoibleLicense"
    }

    if ($cloningEnabled) {
        $publishedClonerDirectory = Join-Path $publishDirectory 'Cloner'
        foreach ($fileName in $requiredClonerFiles) {
            $publishedClonerFile = Join-Path $publishedClonerDirectory $fileName
            if (-not (Test-Path -LiteralPath $publishedClonerFile -PathType Leaf)) {
                throw "Published build is missing required OpenVoice model file: $publishedClonerFile"
            }
        }
    }

    @(
        '',
        'Corresponding source for this release',
        '-------------------------------------',
        "Matching source archive: $sourcePackageName",
        'This archive is generated separately by the release packaging script.',
        'Source distribution requirements are set out in the included GPL/LGPL licenses.'
    ) | Add-Content -LiteralPath (Join-Path $publishDirectory 'THIRD_PARTY_NOTICES.txt') -Encoding UTF8
    $assetsPath = Join-Path $projectDirectory 'obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) { throw "Missing $assetsPath" }

    $assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name })
    $packages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })
    $reviewDirectory = Join-Path $publishDirectory 'THIRD_PARTY_LICENSES/NuGet'
    $metadataDirectory = Join-Path $buildReview 'PackageMetadata'
    $fallbackNotices = @{
        'Microsoft.AspNetCore.OpenApi' = 'AspNetCore-MIT.txt'
        'Microsoft.Extensions.ApiDescription.Server' = 'AspNetCore-MIT.txt'
        'Microsoft.OpenApi' = 'OpenAPI.NET-MIT.txt'
        'NAudio' = 'NAudio-MIT.txt'; 'NAudio.Asio' = 'NAudio-MIT.txt'; 'NAudio.Core' = 'NAudio-MIT.txt'
        'NAudio.Midi' = 'NAudio-MIT.txt'; 'NAudio.Wasapi' = 'NAudio-MIT.txt'; 'NAudio.WinMM' = 'NAudio-MIT.txt'
        'Swashbuckle.AspNetCore' = 'Swashbuckle-MIT.txt'
        'Swashbuckle.AspNetCore.Swagger' = 'Swashbuckle-MIT.txt'
        'Swashbuckle.AspNetCore.SwaggerGen' = 'Swashbuckle-MIT.txt'
        'Swashbuckle.AspNetCore.SwaggerUI' = 'Swashbuckle-MIT.txt'
        'SignalsmithStretch-CS' = 'SignalsmithStretch/Wrapper-MIT-0.txt'
    }
    $report = New-Object System.Collections.Generic.List[string]
    $report.Add("NuGet packages restored for $($build.Name) ($($build.Rid))")
    $report.Add('This report records package metadata and copied notices; inspect native binaries separately.')
    $report.Add('Missing runtime-pack notices stop packaging. Corresponding sources are in the matching separate archive.')
    $report.Add('')

    foreach ($package in $packages) {
        $parts = $package.Name.Split('/')
        $id = $parts[0]
        $version = $parts[1]
        $relativePath = $package.Value.path
        $packageDirectory = $null

        foreach ($folder in $packageFolders) {
            $candidate = Join-Path $folder $relativePath
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $packageDirectory = $candidate
                break
            }
        }

        if (-not $packageDirectory) { throw "Package files unavailable: $id $version" }

        $destination = Join-Path $reviewDirectory (Join-Path $id $version)
        $packageMetadata = Join-Path $metadataDirectory (Join-Path $id $version)
        New-Item -ItemType Directory -Path $packageMetadata -Force | Out-Null
        $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $nuspec) { throw "Missing package metadata: $id $version" }
        Copy-Item -LiteralPath $nuspec.FullName -Destination $packageMetadata -Force

        [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw -Encoding UTF8
        $licenseNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        $licenseUrlNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
        $copyrightNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='copyright']")
        $authorsNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='authors']")
        $licenseValue = if ($licenseNode) { $licenseNode.InnerText.Trim() } else { '' }
        $licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { '' }

        $files = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File |
            Where-Object { $_.Name -match '^(LICENSES?|LICENCES?|COPYING|COPYRIGHT|NOTICES?|THIRD.?PARTY.?NOTICES?)([._-]|$)' })
        if ($licenseType -eq 'file' -and $licenseValue) {
            $declared = Join-Path $packageDirectory $licenseValue
            if (-not (Test-Path -LiteralPath $declared -PathType Leaf)) {
                throw "Declared package license is missing: $id $version ($licenseValue)"
            }
            $files += Get-Item -LiteralPath $declared
        }
        if ($files.Count -eq 0) {
            $fallback = $fallbackNotices[$id]
            $notice = if ($fallback) { Join-Path $publishDirectory "THIRD_PARTY_LICENSES/$fallback" } else { $null }
            if (-not $notice -or -not (Test-Path -LiteralPath $notice -PathType Leaf) -or
                (Get-Item -LiteralPath $notice).Length -eq 0) {
                throw "No license text or reviewed upstream notice is available: $id $version"
            }
            $report.Add("$id $version | upstream notice: THIRD_PARTY_LICENSES/$fallback")
        }

        $rootPath = $packageDirectory.TrimEnd([char[]]@('\', '/'))
        foreach ($file in ($files | Sort-Object FullName -Unique)) {
            $subpath = $file.FullName.Substring($rootPath.Length).TrimStart([char[]]@('\', '/'))
            $target = Join-Path $destination $subpath
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }

        $packageReport = @(
            "$id $version",
            "NuGet: https://www.nuget.org/packages/$id/$version",
            "License metadata: $licenseType $licenseValue",
            "License URL: $(if ($licenseUrlNode) { $licenseUrlNode.InnerText.Trim() } else { '' })",
            "Copyright: $(if ($copyrightNode) { $copyrightNode.InnerText.Trim() } else { '' })",
            "Authors: $(if ($authorsNode) { $authorsNode.InnerText.Trim() } else { '' })",
            "Copied package notice files: $($files.Count)"
        )
        $packageReport | Set-Content -LiteralPath (Join-Path $packageMetadata 'PACKAGE_INFO.txt') -Encoding UTF8
        $report.Add("$id $version | $licenseType $licenseValue | $($files.Count) notice files")
        if ($id -ieq 'SignalsmithStretch-CS') {
            $report.Add((Assert-SignalsmithDistribution -PublishDirectory $publishDirectory `
                -RuntimeIdentifier $build.Rid -PackageDirectory $packageDirectory -PackageVersion $version))
        }
        if ($id -ieq 'SoundTouch.Net' -and $version -ne '2.3.2') {
            throw "SoundTouch version changed; review its corresponding sources: $version"
        }
        if ($id -ieq 'NAudio.Lame') {
            if ($version -ne '2.1.0') { throw "NAudio.Lame version changed; review its native sources: $version" }
            $lameHashes = @{
                'libmp3lame.32.dll' = '91d3c0cc833265066c51408d940faaf76c5b8fc5bda854540a9cc6c88b1d1f20'
                'libmp3lame.64.dll' = '71dba147b6c16d8d0f012b8e657d52e12cb8796a659e1151200d0a6c8b04bce3'
            }
            foreach ($name in $lameHashes.Keys) {
                $native = Join-Path $publishDirectory $name
                if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lameHashes[$name]) {
                    throw "Bundled LAME binary changed; review its corresponding source: $name"
                }
                $report.Add("LAME native asset: $name | SHA256 $($lameHashes[$name]) | NAudio.Lame v2.1.0 source snapshot")
            }
        }


        if (-not $licenseNode -and -not $licenseUrlNode) {
            throw "Package has no license metadata: $id $version"
        }
    }

    $dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
    $runtimeDownloads = @($assets.project.frameworks.PSObject.Properties |
        ForEach-Object { $_.Value.downloadDependencies } |
        Where-Object { $_.name -like 'Microsoft.*.Runtime.*' -or $_.name -like 'Microsoft.NETCore.App.Host.*' })
    $runtimeReviewDirectory = Join-Path $publishDirectory 'THIRD_PARTY_LICENSES/DotNetRuntimePacks'

    foreach ($dependency in $runtimeDownloads) {
        $name = $dependency.name
        $version = (($dependency.version -replace '[\[\]]', '').Split(',')[0]).Trim()
        $runtimeDirectory = $null
        $candidates = @($packageFolders | ForEach-Object { Join-Path $_ (Join-Path $name.ToLowerInvariant() $version) })
        $candidates += (Join-Path (Join-Path $dotnetRoot 'packs') (Join-Path $name $version))

        foreach ($candidate in $candidates) {
            if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }
            $runtimeDirectory = $candidate
            break
        }

        if (-not $runtimeDirectory) {
            throw "Runtime pack notice source not found: $name $version"
        }

        $noticeFiles = @(Get-ChildItem -LiteralPath $runtimeDirectory -Recurse -File |
            Where-Object { $_.Name -match '^(LICENSES?|LICENCES?|COPYING|COPYRIGHT|NOTICES?|THIRD.?PARTY.?NOTICES?)([._-]|$)' })
        if ($noticeFiles.Count -eq 0) {
            throw "Runtime pack has no matching notice files: $name $version"
        }

        $destination = Join-Path $runtimeReviewDirectory (Join-Path $name $version)
        $rootPath = $runtimeDirectory.TrimEnd([char[]]@('\', '/'))
        foreach ($file in $noticeFiles) {
            $subpath = $file.FullName.Substring($rootPath.Length).TrimStart([char[]]@('\', '/'))
            $target = Join-Path $destination $subpath
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }

        $report.Add("Runtime pack $name $version | $($noticeFiles.Count) notice files copied")
    }

    $report | Set-Content -LiteralPath (Join-Path $buildReview 'NUGET_PUBLISH_INVENTORY.txt') -Encoding UTF8
    Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $buildReview 'project.assets.json') -Force
    $symbols = Join-Path $projectDirectory "bin/Release/net10.0/$($build.Rid)/TsubakiTTS.pdb"
    if (Test-Path -LiteralPath $symbols -PathType Leaf) {
        Copy-Item -LiteralPath $symbols -Destination $buildReview -Force
    }
    $publishedHashes = foreach ($file in (Get-ChildItem -LiteralPath $publishDirectory -Recurse -File)) {
        $relative = $file.FullName.Substring($publishDirectory.TrimEnd([char[]]@('\', '/')).Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        "$( (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() )  $relative"
    }
    $publishedHashes | Sort-Object | Set-Content -LiteralPath (Join-Path $buildReview 'PUBLISHED_FILES_SHA256.txt') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveRoot = $build.Artifact
    $sourceRoot = $publishDirectory.TrimEnd([char[]]@('\', '/'))
    $zip = [System.IO.Compression.ZipFile]::Open($archivePath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [void]$zip.CreateEntry("$archiveRoot/")
        foreach ($directory in (Get-ChildItem -LiteralPath $publishDirectory -Recurse -Directory -Force)) {
            $relativePath = $directory.FullName.Substring($sourceRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
            [void]$zip.CreateEntry("$archiveRoot/$relativePath/")
        }
        $fileCount = 0
        foreach ($file in (Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Force)) {
            $relativePath = $file.FullName.Substring($sourceRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, "$archiveRoot/$relativePath", [System.IO.Compression.CompressionLevel]::Optimal)
            $fileCount++
        }
    }
    finally {
        $zip.Dispose()
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $archivedFiles = 0
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.Contains('\') -or
                -not $entry.FullName.StartsWith("$archiveRoot/", [System.StringComparison]::Ordinal)) {
                throw "Invalid ZIP entry path in ${archivePath}: $($entry.FullName)"
            }
            if (-not $entry.FullName.EndsWith('/')) { $archivedFiles++ }
        }
        if ($archivedFiles -ne $fileCount) {
            throw "Incomplete ZIP archive: expected $fileCount files, found $archivedFiles in $archivePath"
        }
    }
    finally {
        $zip.Dispose()
    }
    Write-Host "Published $($build.Name) with $($packages.Count) package records: $archivePath"
}

# Complete the existing separate source archive after all build evidence is available.
$temporaryPackage = "$sourcePackagePath.partial"
try {
    $sourceNames = @('SOURCE_MANIFEST.json') + @($sourceFiles | ForEach-Object { $_.File } | Sort-Object -Unique)
    & tar -czf $temporaryPackage -C $SourcesRoot @sourceNames -C $reviewRun PROJECT_SOURCE BUILD_REVIEW
    if ($LASTEXITCODE -ne 0) { throw "Failed to package corresponding source: $sourcePackagePath" }
    Move-Item -LiteralPath $temporaryPackage -Destination $sourcePackagePath
}
finally {
    Remove-Item -LiteralPath $temporaryPackage -Force -ErrorAction SilentlyContinue
}
Write-Host "Separate source and review download for this release: $sourcePackagePath"
