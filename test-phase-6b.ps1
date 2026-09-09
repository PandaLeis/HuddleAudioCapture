param(
    [string] $BridgeToken,
    [string] $BaseUrl = "http://127.0.0.1:17843",
    [int] $DurationSeconds = 10,
    [string] $OutputPath = "phase-6b-test.wav",
    [switch] $SkipTranscription
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-HuddleJson {
    param(
        [string] $Path,
        [string] $Method = "Get",
        [object] $Body = $null
    )

    $invokeParams = @{
        Uri = "$BaseUrl$Path"
        Method = $Method
        Headers = $headers
    }

    if ($null -ne $Body) {
        $invokeParams.ContentType = "application/json"
        $invokeParams.Body = ($Body | ConvertTo-Json -Compress)
    }

    Invoke-RestMethod @invokeParams
}

if ([string]::IsNullOrWhiteSpace($BridgeToken)) {
    $tokenPath = Join-Path $env:TEMP "HuddleAudioCapture\bridge-token.txt"
    Assert-True (Test-Path $tokenPath) "Bridge token was not provided and token file was not found: $tokenPath"
    $BridgeToken = (Get-Content $tokenPath -Raw).Trim()
}

Assert-True (-not [string]::IsNullOrWhiteSpace($BridgeToken)) "Bridge token is empty."

$headers = @{
    "X-Huddle-Bridge-Token" = $BridgeToken
}

Write-Host "Testing Phase 6B bridge path at $BaseUrl"

try {
    $uri = [uri]$BaseUrl
    $client = New-Object System.Net.Sockets.TcpClient
    $connect = $client.BeginConnect($uri.Host, $uri.Port, $null, $null)
    Assert-True ($connect.AsyncWaitHandle.WaitOne(1000, $false)) "The local bridge is not listening at $BaseUrl."
    $client.EndConnect($connect)
    $client.Close()
}
catch {
    throw "Unable to connect to $BaseUrl. Launch HuddleAudioCapture.exe first, or run: .\publish\win-x64\HuddleAudioCapture.exe --bridge"
}

Write-Host "Calling GET /health..."
$health = Invoke-RestMethod "$BaseUrl/health"
$health | ConvertTo-Json

$sessionId = [guid]::NewGuid().ToString()
$body = @{ sessionId = $sessionId }

Write-Host ""
Write-Host "Start playing computer audio with spoken words now."
Read-Host "Press Enter to start recording"

Write-Host "Starting recording session $sessionId..."
$start = Invoke-HuddleJson -Path "/recording/start" -Method "Post" -Body $body
$start | ConvertTo-Json
Assert-True ($start.success -eq $true) "Start did not report success."
Assert-True ($start.sessionId -eq $sessionId) "Start returned the wrong session ID."

Write-Host "Recording for $DurationSeconds seconds..."
Start-Sleep -Seconds $DurationSeconds

Write-Host "Stopping recording..."
$stop = Invoke-HuddleJson -Path "/recording/stop" -Method "Post" -Body $body
$stop | ConvertTo-Json
Assert-True ($stop.success -eq $true) "Stop did not report success."
Assert-True ($stop.sessionId -eq $sessionId) "Stop returned the wrong session ID."
Assert-True ($stop.hasAudibleAudio -eq $true) "No audible audio was detected."

Write-Host "Checking status..."
$status = Invoke-HuddleJson -Path "/recording/$sessionId/status"
$status | ConvertTo-Json
Assert-True ($status.audioReady -eq $true) "Recording is not marked audioReady."
Assert-True ($status.hasAudibleAudio -eq $true) "Status says no audible audio was detected."

$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
Write-Host "Downloading WAV to $resolvedOutput..."
Invoke-WebRequest "$BaseUrl/recording/$sessionId/audio" `
    -Headers $headers `
    -OutFile $resolvedOutput | Out-Null

$fileInfo = Get-Item $resolvedOutput
Assert-True ($fileInfo.Length -gt 44) "Downloaded WAV is empty or header-only."
Write-Host "Downloaded WAV size: $($fileInfo.Length) bytes"

if ($SkipTranscription) {
    Write-Host "Skipping transcription because -SkipTranscription was supplied."
}
else {
    Write-Host "Submitting recording to transcription flow..."
    $transcribe = Invoke-HuddleJson -Path "/recording/$sessionId/transcribe" -Method "Post"
    $transcribe | ConvertTo-Json

    Assert-True ($transcribe.success -eq $true) "Transcription did not report success."
    Assert-True ($transcribe.sessionId -eq $sessionId) "Transcription returned the wrong session ID."
    Assert-True (-not [string]::IsNullOrWhiteSpace($transcribe.transcript)) "Transcription returned an empty transcript."

    Write-Host ""
    Write-Host "Transcript:"
    Write-Host $transcribe.transcript
}

Write-Host ""
Write-Host "WAV saved for playback testing:"
Write-Host $resolvedOutput

$deleteAnswer = Read-Host "Delete the temporary bridge recording now? Type YES to delete"
if ($deleteAnswer -eq "YES") {
    $delete = Invoke-HuddleJson -Path "/recording/$sessionId" -Method "Delete"
    $delete | ConvertTo-Json
}
else {
    Write-Host "Temporary bridge recording was left in %TEMP%\HuddleAudioCapture."
}
