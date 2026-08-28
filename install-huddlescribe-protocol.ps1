param(
    [string] $ExePath = "$PSScriptRoot\HuddleAudioCapture.exe"
)

$ErrorActionPreference = "Stop"

$resolvedExe = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)

if (-not (Test-Path -LiteralPath $resolvedExe)) {
    throw "HuddleAudioCapture.exe was not found at: $resolvedExe"
}

$protocolKey = "HKCU:\Software\Classes\huddlescribe"
$commandKey = Join-Path $protocolKey "shell\open\command"

New-Item -Path $protocolKey -Force | Out-Null
New-ItemProperty -Path $protocolKey -Name "URL Protocol" -Value "" -PropertyType String -Force | Out-Null
Set-ItemProperty -Path $protocolKey -Name "(default)" -Value "URL:Huddle Scribe Protocol"

New-Item -Path $commandKey -Force | Out-Null
Set-ItemProperty -Path $commandKey -Name "(default)" -Value "`"$resolvedExe`" `"%1`""

Write-Host "Registered huddlescribe:// protocol for:"
Write-Host $resolvedExe
Write-Host ""
Write-Host "Command:"
Get-ItemProperty -LiteralPath $commandKey | Select-Object -ExpandProperty "(default)"
