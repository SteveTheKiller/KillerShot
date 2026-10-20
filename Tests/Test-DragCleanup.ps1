param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutputFolder
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputFolder) { throw 'Use a new output folder.' }
$external = Join-Path $OutputFolder 'external'
$root = Join-Path $OutputFolder 'KillerShot-Drag'
New-Item -ItemType Directory -Path $external, $root | Out-Null
$sentinel = Join-Path $external ('KillerShot ' + [Guid]::NewGuid().ToString('N') + '.png')
[System.IO.File]::WriteAllText($sentinel, 'Preserve external content.')
[System.IO.File]::SetLastWriteTimeUtc($sentinel, [DateTime]::UtcNow.AddDays(-2))
$link = Join-Path $root ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Junction -Path $link -Target $external | Out-Null
[System.IO.Directory]::SetLastWriteTimeUtc($external, [DateTime]::UtcNow.AddDays(-2))
$process = Start-Process -FilePath $AppPath -ArgumentList @('--selftest-drag-cleanup', ('"' + $OutputFolder + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw (Get-Content -LiteralPath (Join-Path $OutputFolder 'drag-cleanup-report.txt') -Raw) }
if (-not (Test-Path -LiteralPath $link) -or [System.IO.File]::ReadAllText($sentinel) -ne 'Preserve external content.') {
    throw 'Cleanup traversed the session junction.'
}
Get-Content -LiteralPath (Join-Path $OutputFolder 'drag-cleanup-report.txt')
Write-Output 'PASS: Session junction and external content preserved.'
