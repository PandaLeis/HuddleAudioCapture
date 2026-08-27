param(
    [string] $BridgeToken,
    [string] $BaseUrl = "http://127.0.0.1:17843",
    [int] $DurationSeconds = 10,
    [string] $OutputPath = "bridge-test.wav"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($BridgeToken)) {
    $tokenPath = Join-Path $env:TEMP "HuddleAudioCapture\bridge-token.txt"
    if (-not (Test-Path $tokenPath)) {
        throw "Bridge token was not provided and token file was not found: $tokenPath"
    }

    $BridgeToken = (Get-Content $tokenPath -Raw).Trim()
}

$headers = @{
    "X-Huddle-Bridge-Token" = $BridgeToken
}

Write-Host "Testing bridge at $BaseUrl"
try {
    $uri = [uri]$BaseUrl
    $client = New-Object System.Net.Sockets.TcpClient
    $connect = $client.BeginConnect($uri.Host, $uri.Port, $null, $null)
    if (-not $connect.AsyncWaitHandle.WaitOne(1000, $false)) {
        throw "The local bridge is not listening at $BaseUrl."
    }

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
$body = @{ sessionId = $sessionId } | ConvertTo-Json

Write-Host ""
Write-Host "Start playing computer audio now."
Read-Host "Press Enter to start recording"

Write-Host "Calling POST /recording/start for session $sessionId..."
$start = Invoke-RestMethod "$BaseUrl/recording/start" `
    -Method Post `
    -Headers $headers `
    -ContentType "application/json" `
    -Body $body
$start | ConvertTo-Json

Write-Host "Recording for $DurationSeconds seconds..."
Start-Sleep -Seconds $DurationSeconds

Write-Host "Calling GET /recording/$sessionId/status..."
$status = Invoke-RestMethod "$BaseUrl/recording/$sessionId/status" -Headers $headers
$status | ConvertTo-Json

Write-Host "Calling POST /recording/stop..."
$stop = Invoke-RestMethod "$BaseUrl/recording/stop" `
    -Method Post `
    -Headers $headers `
    -ContentType "application/json" `
    -Body $body
$stop | ConvertTo-Json

$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
Write-Host "Downloading WAV to $resolvedOutput..."
Invoke-WebRequest "$BaseUrl/recording/$sessionId/audio" `
    -Headers $headers `
    -OutFile $resolvedOutput | Out-Null

$fileInfo = Get-Item $resolvedOutput
Write-Host "Downloaded WAV size: $($fileInfo.Length) bytes"
Write-Host "Play this file to confirm it contains computer audio:"
Write-Host $resolvedOutput

$deleteAnswer = Read-Host "Delete the temporary bridge recording now? Type YES to delete"
if ($deleteAnswer -eq "YES") {
    $delete = Invoke-RestMethod "$BaseUrl/recording/$sessionId" `
        -Method Delete `
        -Headers $headers
    $delete | ConvertTo-Json
}
else {
    Write-Host "Temporary bridge recording was left in %TEMP%\HuddleAudioCapture."
}
