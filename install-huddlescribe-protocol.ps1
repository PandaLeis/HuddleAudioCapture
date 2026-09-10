param(
    [string] $ExePath = "$PSScriptRoot\HuddleAudioCapture.exe",
    [string] $TranscriptionFlowUrl = "",
    [switch] $Uninstall
)

$ErrorActionPreference = "Stop"

$protocolKey = "HKCU:\Software\Classes\huddlescribe"
$commandKey = Join-Path $protocolKey "shell\open\command"
$configFolder = Join-Path $env:APPDATA "HuddleAudioCapture"
$configFile = Join-Path $configFolder "appsettings.json"

if ($Uninstall) {
    if (Test-Path -LiteralPath $protocolKey) {
        Remove-Item -LiteralPath $protocolKey -Recurse -Force
        Write-Host "Removed huddlescribe:// protocol registration."
    }
    else {
        Write-Host "huddlescribe:// protocol registration was not present."
    }

    Write-Host "User configuration was left in place:"
    Write-Host $configFile
    exit 0
}

$resolvedExe = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)

if (-not (Test-Path -LiteralPath $resolvedExe)) {
    throw "HuddleAudioCapture.exe was not found at: $resolvedExe"
}

New-Item -Path $protocolKey -Force | Out-Null
Set-Item -Path $protocolKey -Value "URL:Huddle Scribe Protocol"
New-ItemProperty -Path $protocolKey -Name "URL Protocol" -Value "" -PropertyType String -Force | Out-Null

New-Item -Path $commandKey -Force | Out-Null
Set-Item -Path $commandKey -Value "`"$resolvedExe`" `"%1`""

if (-not [string]::IsNullOrWhiteSpace($TranscriptionFlowUrl)) {
    New-Item -ItemType Directory -Path $configFolder -Force | Out-Null

    $config = [ordered]@{
        huddleTranscriptionFlowUrl = $TranscriptionFlowUrl
    }

    $config |
        ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath $configFile -Encoding UTF8

    Write-Host "Wrote transcription endpoint configuration to:"
    Write-Host $configFile
}
else {
    Write-Host "No transcription endpoint was written. Pass -TranscriptionFlowUrl to configure production transcription."
}

Write-Host "Registered huddlescribe:// protocol for:"
Write-Host $resolvedExe
Write-Host ""
Write-Host "Command:"
(Get-Item -LiteralPath $commandKey).GetValue("")
