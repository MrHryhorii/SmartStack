# Exercises concurrent HTTP synthesis on an already running Tsubaki server.
# The running server determines the backend. The script saves WAV responses and a JSON report.
param(
    [string]$BaseUrl = 'http://localhost:5045',
    [string]$Voice,
    [ValidateRange(1, 32)][int]$Requests = 6,
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('Tsubaki-WebGPU-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
# Reuse one client and allow enough server connections for the whole request batch.
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.MaxConnectionsPerServer = $Requests
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMinutes(3)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$base = $BaseUrl.TrimEnd('/')
$results = New-Object 'System.Collections.Generic.List[object]'
$failures = 0

try {
    # Test the requested voice, or Piper and the first available clone in separate batches.
    $available = ($client.GetStringAsync("$base/tsbk/audio/voices").GetAwaiter().GetResult() | ConvertFrom-Json).voices
    if ($Voice -and $available -notcontains $Voice) { throw "Voice is not available: $Voice" }
    $voices = @('piper_base')
    if ($Voice) { $voices = @($Voice) }
    if (-not $Voice) {
        $clone = $available | Where-Object { $_ -ne 'piper_base' } | Select-Object -First 1
        if ($clone) { $voices += $clone }
        if (-not $clone) { Write-Warning 'No cloned voice is available; only Piper will be tested.' }
    }

    $voiceIndex = 0
    foreach ($name in $voices) {
        $voiceIndex++
        $pending = New-Object 'System.Collections.Generic.List[object]'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            # Start every asynchronous POST before awaiting any response.
            for ($index = 1; $index -le $Requests; $index++) {
                $body = @{
                    model = 'tts-1'; voice = $name; response_format = 'wav'; stream = $false
                    input = "Parallel request number $index. The engine should keep this voice separate while several requests run together."
                } | ConvertTo-Json -Compress
                $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
                $task = $client.PostAsync("$base/v1/audio/speech", $content)
                $pending.Add([pscustomobject]@{ Index = $index; Task = $task; Content = $content })
            }
            # Check each HTTP status and WAV header, then retain the audio for inspection.
            foreach ($request in $pending) {
                $response = $null
                try {
                    $response = $request.Task.GetAwaiter().GetResult()
                    $response.EnsureSuccessStatusCode() | Out-Null
                    $audio = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                    if ($audio.Length -le 44 -or [Text.Encoding]::ASCII.GetString($audio, 0, 4) -ne 'RIFF') {
                        throw 'Response is not a nonempty WAV file.'
                    }
                    $path = Join-Path $outputRoot ("voice-{0}-request-{1}.wav" -f $voiceIndex, $request.Index)
                    [IO.File]::WriteAllBytes($path, $audio)
                    $results.Add([pscustomobject]@{ Voice = $name; Request = $request.Index; Status = 'passed'; Bytes = $audio.Length; File = $path })
                }
                catch {
                    $failures++
                    $results.Add([pscustomobject]@{ Voice = $name; Request = $request.Index; Status = 'failed'; Error = $_.Exception.Message })
                }
                finally { if ($response) { $response.Dispose() } }
            }
        }
        finally {
            # Request content belongs to the batch. Each response is disposed after processing.
            foreach ($request in $pending) { $request.Content.Dispose() }
            $timer.Stop()
        }
        Write-Host ("Voice: {0}; parallel requests: {1}; batch duration: {2:N3} s" -f $name, $Requests, $timer.Elapsed.TotalSeconds)
    }
    # Report all requests together and return a failing exit code if any request failed.
    $results | Format-Table Voice, Request, Status, Bytes, Error -AutoSize
    [IO.File]::WriteAllText((Join-Path $outputRoot 'results.json'), ($results | ConvertTo-Json -Depth 4), [Text.Encoding]::UTF8)
    Write-Host "Output: $outputRoot"
    if ($failures -gt 0) { exit 1 }
}
finally { $client.Dispose() }
