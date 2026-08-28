param(
    [string] $BaseUrl = "http://127.0.0.1:17843"
)

$ErrorActionPreference = "Continue"

Write-Host "Huddle Audio Capture diagnostics"
Write-Host "================================"
Write-Host ""

$commandKey = "HKCU:\Software\Classes\huddlescribe\shell\open\command"
Write-Host "Protocol registration:"
if (Test-Path -LiteralPath $commandKey) {
    $command = Get-ItemProperty -LiteralPath $commandKey | Select-Object -ExpandProperty "(default)"
    Write-Host "  huddlescribe:// is registered"
    Write-Host "  $command"
}
else {
    Write-Host "  huddlescribe:// is NOT registered"
}

Write-Host ""
Write-Host "Running processes:"
$processes = Get-Process HuddleAudioCapture -ErrorAction SilentlyContinue
if ($processes) {
    $processes | Select-Object Id, Path | Format-Table -AutoSize
}
else {
    Write-Host "  HuddleAudioCapture.exe is not currently running"
}

Write-Host ""
Write-Host "Bridge health:"
try {
    Invoke-RestMethod "$BaseUrl/health" | Format-List
}
catch {
    Write-Host "  Could not reach $BaseUrl/health"
    Write-Host "  $($_.Exception.Message)"
}

Write-Host ""
Write-Host "Bridge token file:"
$tokenPath = Join-Path $env:TEMP "HuddleAudioCapture\bridge-token.txt"
if (Test-Path -LiteralPath $tokenPath) {
    Write-Host "  Found: $tokenPath"
}
else {
    Write-Host "  Missing: $tokenPath"
}

Write-Host ""
Write-Host "Temp bridge log:"
$logPath = Join-Path $env:TEMP "HuddleAudioCapture\bridge.log"
if (Test-Path -LiteralPath $logPath) {
    Write-Host "  Found: $logPath"
    Write-Host ""
    Get-Content -LiteralPath $logPath -Tail 20
}
else {
    Write-Host "  Missing: $logPath"
}
