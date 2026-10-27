$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'UninstallReceipt.ps1')
Invoke-KillerShotUninstall -InstallDirectory (Join-Path $env:ProgramFiles 'KillerShot')
