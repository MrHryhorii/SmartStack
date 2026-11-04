param([string]$SourceDirectory, [string]$PreparationScript)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$tokens = $null
$errors = $null
if (-not $PreparationScript) { $PreparationScript = Join-Path $project 'tools/Prepare-CorrespondingSources.ps1' }
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $PreparationScript, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw "Source script syntax errors: $errors" }
foreach ($name in @('Assert-Hash', 'Save-Download', 'Get-AudioLibrarySources')) {
    $definition = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing source download helper: $name" }
    Invoke-Expression $definition.Extent.Text
}
$publishAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $project 'tools/Publish-WithLicenses.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw "Publish script syntax errors: $errors" }
$moveSymbols = $publishAst.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Move-PublishedSymbols'
}, $true)
if (-not $moveSymbols) { throw 'Missing publish symbol routing helper.' }
Invoke-Expression $moveSymbols.Extent.Text

$root = Join-Path ([IO.Path]::GetTempPath()) ('Tsubaki-DownloadChecks-' + [guid]::NewGuid().ToString('N'))
$primary = 'https://primary.invalid/source.tar.gz'
$mirror = 'https://mirror.invalid/source.tar.gz'
$valid = [Text.Encoding]::UTF8.GetBytes('Verified source download fixture.')
$invalid = [Text.Encoding]::UTF8.GetBytes('<html>Download gateway response</html>')
$sha = [Security.Cryptography.SHA256]::Create()
try { $expected = ([BitConverter]::ToString($sha.ComputeHash($valid))).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose() }
$script:requests = New-Object 'System.Collections.Generic.List[string]'
$script:checks = 0
$failures = 0

function Assert-Condition([bool]$Condition, [string]$Message) {
    $script:checks++
    if (-not $Condition) { throw $Message }
}

function Assert-VerifiedFile {
    Assert-Condition (Test-Path -LiteralPath $target -PathType Leaf) 'Verified download was not committed.'
    Assert-Condition ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $expected) 'Unverified bytes were committed.'
}

# Replace only the transport; execute the production cache, hash and commit logic.
function Invoke-WebRequest {
    param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing, [int]$TimeoutSec)
    $script:requests.Add($Uri)
    if (-not $UseBasicParsing) { throw 'Downloads must use basic parsing for Windows PowerShell.' }
    if ($TimeoutSec -le 0) { throw 'Downloads must have a finite timeout.' }
    $response = $script:responses[$Uri]
    if ($null -eq $response) { throw "Unexpected download: $Uri" }
    Assert-Condition ($OutFile -ne $script:target) 'Transport wrote directly to the completed cache file.'
    if ($response.ContainsKey('Bytes')) { [IO.File]::WriteAllBytes($OutFile, [byte[]]$response.Bytes) }
    if ($response.Error) { throw $response.Error }
}

$cases = @(
    @{ Name = 'missing cache'; Run = {
        Save-Download $primary $target $expected
        Assert-VerifiedFile
        Assert-Condition ($requests.Count -eq 1) 'Missing source was not fetched exactly once.'
    } },
    @{ Name = 'verified cache reuse'; Run = {
        [IO.File]::WriteAllBytes($target, $valid)
        $stamp = (Get-Item -LiteralPath $target).LastWriteTimeUtc
        Save-Download $primary $target $expected
        Assert-VerifiedFile
        Assert-Condition ($requests.Count -eq 0) 'Verified cache was downloaded again.'
        Assert-Condition ((Get-Item -LiteralPath $target).LastWriteTimeUtc -eq $stamp) 'Verified cache was rewritten.'
    } },
    @{ Name = 'repair invalid cache'; Run = {
        [IO.File]::WriteAllBytes($target, $invalid)
        Save-Download $primary $target $expected
        Assert-VerifiedFile
        Assert-Condition ($requests.Count -eq 1) 'Invalid cache prevented a verified retry.'
    } },
    @{ Name = 'HTML response then mirror'; Run = {
        $script:responses[$primary] = @{ Bytes = $invalid }
        Save-Download $primary $target $expected @($mirror)
        Assert-VerifiedFile
        Assert-Condition (($requests -join ',') -eq "$primary,$mirror") 'Hash failure did not advance to the mirror.'
    } },
    @{ Name = 'transport failure then mirror'; Run = {
        $script:responses[$primary] = @{ Error = 'HTTP fixture failure' }
        Save-Download $primary $target $expected @($mirror)
        Assert-VerifiedFile
        Assert-Condition ($requests.Count -eq 2) 'Transport failure did not advance to the mirror.'
    } },
    @{ Name = 'interrupted transfer then mirror'; Run = {
        $script:responses[$primary] = @{ Bytes = $invalid; Error = 'Transfer interrupted' }
        Save-Download $primary $target $expected @($mirror)
        Assert-VerifiedFile
        Assert-Condition ($requests.Count -eq 2) 'Interrupted transfer was not retried through the mirror.'
    } },
    @{ Name = 'empty response then mirror'; Run = {
        $script:responses[$primary] = @{ Bytes = [byte[]]@() }
        Save-Download $primary $target $expected @($mirror)
        Assert-VerifiedFile
        Assert-Condition ($requests.Count -eq 2) 'Empty source response was accepted.'
    } },
    @{ Name = 'reject all invalid responses'; Run = {
        $script:responses[$primary] = @{ Bytes = $invalid }
        $script:responses[$mirror] = @{ Bytes = $invalid }
        $rejected = $false
        try { Save-Download $primary $target $expected @($mirror) }
        catch { $rejected = $_.Exception.Message -like '*Unexpected SHA-256*' }
        Assert-Condition $rejected 'Invalid source downloads were accepted.'
        Assert-Condition (-not (Test-Path -LiteralPath $target)) 'A failed download created a completed cache file.'
        Assert-Condition ($requests.Count -eq 2) 'Source attempts were not bounded by the configured URLs.'
    } },
    @{ Name = 'retain invalid cache until replacement is verified'; Run = {
        [IO.File]::WriteAllBytes($target, $invalid)
        $script:responses[$primary] = @{ Bytes = $invalid }
        $rejected = $false
        try { Save-Download $primary $target $expected }
        catch { $rejected = $_.Exception.Message -like '*Unexpected SHA-256*' }
        Assert-Condition $rejected 'Invalid cached/downloaded content was accepted.'
        Assert-Condition ([Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) -eq [Convert]::ToBase64String($invalid)) 'A failed replacement changed the cached file.'
    } },
    @{ Name = 'clean interrupted transfer without mirror'; Run = {
        $script:responses[$primary] = @{ Bytes = $invalid; Error = 'Transfer interrupted' }
        $rejected = $false
        try { Save-Download $primary $target $expected }
        catch { $rejected = $_.Exception.Message -like '*Transfer interrupted*' }
        Assert-Condition $rejected 'Interrupted transfer was accepted.'
        Assert-Condition (-not (Test-Path -LiteralPath $target)) 'Interrupted transfer created a completed cache file.'
    } },
    @{ Name = 'unpinned download compatibility'; Run = {
        Save-Download $primary $target
        Assert-VerifiedFile
    } },
    @{ Name = 'unpinned cache compatibility'; Run = {
        [IO.File]::WriteAllBytes($target, $valid)
        Save-Download $primary $target
        Assert-VerifiedFile
        Assert-Condition ($requests.Count -eq 0) 'Existing unpinned cache behavior changed.'
    } },
    @{ Name = 'route native and nested debug symbols to review'; Run = {
        $publish = Join-Path $caseRoot 'publish'
        $review = Join-Path $caseRoot 'review'
        $nested = Join-Path $publish 'runtimes/win-x64/native'
        New-Item -ItemType Directory -Path $nested -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $publish 'runtime.dll'), $valid)
        [IO.File]::WriteAllBytes((Join-Path $publish 'runtime.pdb'), $valid)
        [IO.File]::WriteAllBytes((Join-Path $nested 'runtime.PDB'), $valid)
        Move-PublishedSymbols -PublishDirectory $publish -ReviewDirectory $review
        Assert-Condition (Test-Path -LiteralPath (Join-Path $review 'Symbols/runtime.pdb')) 'Native symbols were not retained for review.'
        Assert-Condition (Test-Path -LiteralPath (Join-Path $review 'Symbols/runtimes/win-x64/native/runtime.PDB')) 'Nested symbols lost their relative path.'
        Assert-Condition (@(Get-ChildItem -LiteralPath $publish -Recurse -File -Force | Where-Object { $_.Extension -ieq '.pdb' }).Count -eq 0) 'Debug symbols remained in client files.'
        Assert-Condition ((Get-FileHash (Join-Path $publish 'runtime.dll') -Algorithm SHA256).Hash -eq $expected) 'Routing symbols changed a runtime binary.'
    } },
    @{ Name = 'publish helper without symbols'; Run = {
        $publish = Join-Path $caseRoot 'publish'
        $review = Join-Path $caseRoot 'review'
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $publish 'runtime.dll'), $valid)
        Move-PublishedSymbols -PublishDirectory $publish -ReviewDirectory $review
        Assert-Condition ((Get-FileHash (Join-Path $publish 'runtime.dll') -Algorithm SHA256).Hash -eq $expected) 'A symbol-free client binary changed.'
        Assert-Condition (-not (Test-Path -LiteralPath $review)) 'An empty symbol review directory was created.'
    } }
)

if ($SourceDirectory) {
    $cases += @{ Name = 'LAME cache repair through production audio-source inventory'; Run = {
        foreach ($archive in @('soundtouch.net-98e5b8fd2f8efed0ddf7c8f66b435bfb231659dc.tar.gz',
            'NAudio.Lame-5b5bb009d8c63ef8ab95cb36ca9105c8acc1c5fc.tar.gz', 'lame-3.100.tar.gz')) {
            Copy-Item (Join-Path $SourceDirectory $archive) $caseRoot
        }
        $script:target = Join-Path $caseRoot 'lame-3.100.tar.gz'
        [IO.File]::WriteAllBytes($target, $invalid)
        $upstream = 'https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz'
        $fallback = 'https://download.videolan.org/pub/contrib/lame/lame-3.100.tar.gz'
        $script:responses[$upstream] = @{ Bytes = $invalid }
        $script:responses[$fallback] = @{ Bytes = [IO.File]::ReadAllBytes((Join-Path $SourceDirectory 'lame-3.100.tar.gz')) }
        $entries = @(Get-AudioLibrarySources $caseRoot)
        Assert-Condition ($entries.Count -eq 3) 'Audio-source inventory changed.'
        foreach ($entry in $entries) { Assert-Hash (Join-Path $caseRoot $entry.File) $entry.Sha256 }
        Assert-Condition (($requests -join ',') -eq "$upstream,$fallback") 'LAME repair did not use its verified mirror.'
    } }
}

try {
    foreach ($case in $cases) {
        $caseRoot = Join-Path $root ($case.Name.Replace(' ', '-'))
        New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
        $script:target = Join-Path $caseRoot 'source.tar.gz'
        $script:responses = @{ $primary = @{ Bytes = $valid }; $mirror = @{ Bytes = $valid } }
        $requests.Clear()
        try {
            & $case.Run
            Assert-Condition (@(Get-ChildItem -LiteralPath $caseRoot -Filter '*.partial' -File).Count -eq 0) 'Temporary download files were retained.'
            Write-Output "PASS $($case.Name)"
        }
        catch {
            $failures++
            Write-Output "FAIL $($case.Name): $($_.Exception.Message)"
        }
    }
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
Write-Output "Source/publish helpers: $($cases.Count - $failures)/$($cases.Count) scenarios passed; $checks assertions; $failures failures."
if ($failures -ne 0) { exit 1 }
