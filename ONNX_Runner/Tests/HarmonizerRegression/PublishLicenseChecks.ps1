param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$SourceDirectory
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$script = Join-Path $project 'tools/Publish-WithLicenses.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw "Publish script syntax errors: $errors" }
$definition = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-SignalsmithDistribution'
}, $true)
if (-not $definition) { throw 'Missing native distribution validation.' }
Invoke-Expression $definition.Extent.Text
$result = Assert-SignalsmithDistribution $PublishDirectory $RuntimeIdentifier $PackageDirectory
$result | Write-Output
$nativeName = if ($RuntimeIdentifier -eq 'win-x64') { 'SignalsmithStretch.dll' } else { 'libSignalsmithStretch.so' }
$noticeNames = @('Wrapper-MIT-0.txt', 'Stretch-MIT.txt', 'Linear-MIT.txt', 'PROVENANCE.txt')
$checks = 2
foreach ($missing in $noticeNames) {
    $trial = Join-Path $WorkDirectory $missing
    $notices = Join-Path $trial 'THIRD_PARTY_LICENSES/SignalsmithStretch'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    Copy-Item (Join-Path $PublishDirectory $nativeName) $trial
    foreach ($name in $noticeNames | Where-Object { $_ -ne $missing }) {
        Copy-Item (Join-Path $PublishDirectory "THIRD_PARTY_LICENSES/SignalsmithStretch/$name") $notices
    }
    $rejected = $false
    try { $null = Assert-SignalsmithDistribution $trial $RuntimeIdentifier $PackageDirectory }
    catch { $rejected = $_.Exception.Message -like '*missing a Signalsmith component notice*' }
    if (-not $rejected) { throw "Missing notice was accepted: $missing" }
    $checks++
}
$trial = Join-Path $WorkDirectory 'changed-native'
New-Item -ItemType Directory -Path $trial -Force | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $trial $nativeName), [byte[]]@(0, 1, 2))
$rejected = $false
try { $null = Assert-SignalsmithDistribution $trial $RuntimeIdentifier $PackageDirectory }
catch { $rejected = $_.Exception.Message -like '*differs from the restored package*' }
if (-not $rejected) { throw 'A changed native binary was accepted.' }
$checks++
$rejected = $false
try { $null = Assert-SignalsmithDistribution $PublishDirectory 'unsupported-rid' $PackageDirectory }
catch { $rejected = $_.Exception.Message -like '*has not been reviewed*' }
if (-not $rejected) { throw 'An unreviewed runtime was accepted.' }
$checks++
$rejected = $false
try { $null = Assert-SignalsmithDistribution $PublishDirectory $RuntimeIdentifier $PackageDirectory 'unreviewed-version' }
catch { $rejected = $_.Exception.Message -like '*version changed*' }
if (-not $rejected) { throw 'Unreviewed package versions bypassed source/notice validation.' }
$checks++
if ($SourceDirectory) {
    $prepare = Join-Path $project 'tools/Prepare-CorrespondingSources.ps1'
    $sourceAst = [System.Management.Automation.Language.Parser]::ParseFile($prepare, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "Source script syntax errors: $errors" }
    foreach ($name in @('Assert-Hash', 'Save-Download', 'Get-SignalsmithSources')) {
        $function = $sourceAst.Find({ param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $true)
        if (-not $function) { throw "Source preparation helper is missing: $name" }
        Invoke-Expression $function.Extent.Text
    }
    $checks++
    $cache = Join-Path $WorkDirectory 'source-cache'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    Copy-Item (Join-Path $SourceDirectory '*.tar.gz') $cache -Force
    $entries = @(Get-SignalsmithSources $cache)
    if ($entries.Count -ne 3) { throw 'Source preparation lost a component.' }
    $checks++
    $again = @(Get-SignalsmithSources $cache)
    if (($entries | ConvertTo-Json -Depth 4) -ne ($again | ConvertTo-Json -Depth 4)) { throw 'Source reuse changed its inventory.' }
    $checks++
    [System.IO.File]::WriteAllText((Join-Path $cache $entries[0].File), 'Invalid source fixture')
    $rejected = $false
    try { $null = @(Get-SignalsmithSources $cache) }
    catch { $rejected = $_.Exception.Message -like '*Unexpected SHA-256*' }
    if (-not $rejected) { throw 'A changed source archive was accepted.' }
    $checks++

    # Exercise a legacy cache upgrade using inert placeholders for unrelated components.
    $legacy = Join-Path $WorkDirectory 'legacy-source-fixture'
    New-Item -ItemType Directory -Path $legacy -Force | Out-Null
    Copy-Item (Join-Path $SourceDirectory '*.tar.gz') $legacy -Force
    $placeholder = Join-Path $legacy 'unrelated-source-fixture.txt'
    [System.IO.File]::WriteAllText($placeholder, 'Test fixture only. This is not distribution source.')
    $hash = (Get-FileHash $placeholder -Algorithm SHA256).Hash.ToLowerInvariant()
    $entry = @{ File = 'unrelated-source-fixture.txt'; Sha256 = $hash }
    Copy-Item $placeholder (Join-Path $legacy 'SOURCE_REVISIONS.txt') -Force
    $manifest = @{
        EspeakDllSha256 = '9588480f8197df62fd8461a8431f8eaec6e8e7749c5ffcbe7fee656fe40a2189'
        Mpg123BundleSha256 = 'c13a80f0455795eb0c8a1c7844fb5e567cf37acc00fdd3e1f3e6b89f7b4601ce'
        OggOpusBundleSha256 = 'c5055d3410ca02728d10708e154b47219c38ceef52155f18702e34204150651f'
        Archives = @($entry, $entry, $entry, $entry)
        NpmPackages = @($entry, $entry)
        SupplementalPackages = @($entry)
        SourceRevisionsSha256 = $hash
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $legacy 'SOURCE_MANIFEST.json') -Encoding UTF8
    & $prepare -ProjectDirectory $project -SourcesRoot $legacy
    $updated = Get-Content (Join-Path $legacy 'SOURCE_MANIFEST.json') -Raw | ConvertFrom-Json
    if (@($updated.SignalsmithSources).Count -ne 3 -or @($updated.Archives).Count -ne 4) { throw 'Legacy cache upgrade lost source records.' }
    foreach ($component in $updated.SignalsmithSources) { Assert-Hash (Join-Path $legacy $component.File) $component.Sha256 }
    $checks++
}
Write-Output "Signalsmith distribution: $checks/$checks checks passed for $RuntimeIdentifier."
