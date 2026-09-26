param(
    [ValidateSet('All', 'Windows-CPU', 'Windows-DML', 'Windows-WebGPU', 'Linux-CPU', 'Linux-CUDA', 'Linux-WebGPU')]
    [string]$Variant = 'All',
    [string]$ProjectDirectory,
    [string]$OutputRoot,
    [string]$ReviewRoot,
    [string]$SourcesRoot
)

$ErrorActionPreference = 'Stop'
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
$outputRoot = $OutputRoot
$reviewRoot = $ReviewRoot
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $outputRoot $runId
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

$modelDirectory = Join-Path $projectDirectory 'Model'
$models = @(Get-ChildItem -LiteralPath $modelDirectory -Filter '*.onnx' -File -ErrorAction SilentlyContinue)
if ($models.Count -eq 0) {
    throw "A ready-to-run release needs a Piper .onnx model in $modelDirectory. Restore the model from your complete project before publishing."
}
foreach ($model in $models) {
    if (-not (Test-Path -LiteralPath "$($model.FullName).json" -PathType Leaf)) {
        throw "Missing Piper model configuration: $($model.FullName).json"
    }
}
$phoibleFile = Join-Path $projectDirectory 'PHOIBLE/phoible.csv'
if (-not (Test-Path -LiteralPath $phoibleFile -PathType Leaf)) {
    throw "A ready-to-run release needs the PHOIBLE dataset at $phoibleFile. Restore it from your complete project before publishing."
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
$sourceFiles = @($sourceManifest.Archives) + @($sourceManifest.NpmPackages) + @($sourceManifest.SupplementalPackages) + @([pscustomobject]@{
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

$sourcePackageName = 'Tsubaki-Corresponding-Sources-{0}-{1}-{2}.tar.gz' -f `
    $sourceManifest.EspeakDllSha256.Substring(0, 12), `
    $sourceManifest.Mpg123BundleSha256.Substring(0, 12), `
    $sourceManifest.OggOpusBundleSha256.Substring(0, 12)
$sourcePackagePath = Join-Path (Split-Path $SourcesRoot -Parent) $sourcePackageName
if (-not (Test-Path -LiteralPath $sourcePackagePath -PathType Leaf)) {
    $temporaryPackage = "$sourcePackagePath.partial"
    try {
        $sourceNames = @('SOURCE_MANIFEST.json') + @($sourceFiles | ForEach-Object { $_.File })
        & tar -czf $temporaryPackage -C $SourcesRoot @sourceNames
        if ($LASTEXITCODE -ne 0) { throw "Failed to package corresponding source: $sourcePackagePath" }
        Move-Item -LiteralPath $temporaryPackage -Destination $sourcePackagePath
    }
    finally {
        Remove-Item -LiteralPath $temporaryPackage -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "Separate source download for this release: $sourcePackagePath"

foreach ($build in $variants) {
    if ($Variant -ne 'All' -and $Variant -ne $build.Name) { continue }

    $publishDirectory = Join-Path $runRoot $build.Artifact
    $archivePath = Join-Path $runRoot "$($build.Artifact).zip"
    if ((Test-Path -LiteralPath $publishDirectory) -or (Test-Path -LiteralPath $archivePath)) {
        throw "Release output already exists: $($build.Artifact). Choose another -OutputRoot or start a new run."
    }
    $arguments = @('publish', $projectFile, '-c', 'Release', '-r', $build.Rid,
        '--self-contained', 'true', '--force', '-o', $publishDirectory)
    if ($build.Property) { $arguments += $build.Property }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($build.Name)." }

    foreach ($model in $models) {
        $relativeModel = Join-Path 'Model' $model.Name
        $publishedModel = Join-Path $publishDirectory $relativeModel
        if (-not (Test-Path -LiteralPath $publishedModel -PathType Leaf)) {
            throw "Published build is missing Piper model: $publishedModel"
        }
        if (-not (Test-Path -LiteralPath "$publishedModel.json" -PathType Leaf)) {
            throw "Published build is missing Piper model configuration: $publishedModel.json"
        }
    }
    $publishedPhoible = Join-Path $publishDirectory 'PHOIBLE/phoible.csv'
    if (-not (Test-Path -LiteralPath $publishedPhoible -PathType Leaf)) {
        throw "Published build is missing PHOIBLE data: $publishedPhoible"
    }

    Copy-Item -LiteralPath $manifestFile -Destination (Join-Path $publishDirectory 'SOURCE_MANIFEST.json') -Force
    @(
        "Corresponding source for the bundled native and browser binaries is a separate download: $sourcePackageName",
        'The distributor must provide the matching source package alongside this binary download, free of charge.',
        'For GitHub releases, attach the package once as a separate asset of the same release.',
        'See SOURCE_MANIFEST.json for the exact bundled binary hashes and source archive checksums.'
    ) | Set-Content -LiteralPath (Join-Path $publishDirectory 'SOURCE_ACCESS.txt') -Encoding UTF8
    $assetsPath = Join-Path $projectDirectory 'obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) { throw "Missing $assetsPath" }

    $assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name })
    $packages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })
    $reviewDirectory = Join-Path $publishDirectory 'THIRD_PARTY_LICENSES/NuGet'
    New-Item -ItemType Directory -Path $reviewDirectory -Force | Out-Null
    $report = New-Object System.Collections.Generic.List[string]
    $report.Add("NuGet packages restored for $($build.Name) ($($build.Rid))")
    $report.Add('This report records package metadata and copied notices; inspect native binaries separately.')
    $report.Add('Runtime-pack notices are copied where found; check missing-pack lines and GPL/LGPL corresponding sources separately.')
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
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $nuspec) { throw "Missing package metadata: $id $version" }
        Copy-Item -LiteralPath $nuspec.FullName -Destination $destination -Force

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
        $packageReport | Set-Content -LiteralPath (Join-Path $destination 'PACKAGE_INFO.txt') -Encoding UTF8
        $report.Add("$id $version | $licenseType $licenseValue | $($files.Count) notice files")

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
            $report.Add("Runtime pack notice source not found: $name $version")
            Write-Warning "Runtime pack notice source not found: $name $version"
            continue
        }

        $noticeFiles = @(Get-ChildItem -LiteralPath $runtimeDirectory -Recurse -File |
            Where-Object { $_.Name -match '^(LICENSES?|LICENCES?|COPYING|COPYRIGHT|NOTICES?|THIRD.?PARTY.?NOTICES?)([._-]|$)' })
        if ($noticeFiles.Count -eq 0) {
            $report.Add("Runtime pack has no matching notice files: $name $version")
            Write-Warning "Runtime pack has no matching notice files: $name $version"
            continue
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

    $report | Set-Content -LiteralPath (Join-Path $publishDirectory 'NUGET_PUBLISH_INVENTORY.txt') -Encoding UTF8
    @(
        'This folder contains the application, project notices, and NuGet package notices.',
        "Corresponding source is a separate download named $sourcePackageName; see SOURCE_ACCESS.txt.",
        'Check NUGET_PUBLISH_INVENTORY.txt for runtime-pack notice warnings and THIRD_PARTY_NOTICES.txt for component-specific terms.'
    ) | Set-Content -LiteralPath (Join-Path $publishDirectory 'RELEASE_CONTENTS.txt') -Encoding UTF8
    New-Item -ItemType Directory -Path $reviewRoot -Force | Out-Null
    Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $reviewRoot "$($build.Name)-$runId.project.assets.json") -Force
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