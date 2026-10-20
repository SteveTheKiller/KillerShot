param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutputFolder
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputFolder) { throw 'Use a new output folder.' }
New-Item -ItemType Directory -Path $OutputFolder | Out-Null
foreach ($collision in @($false, $true)) {
    $caseFolder = Join-Path $OutputFolder $(if ($collision) { 'publication-failure' } else { 'audio-warning' })
    $destination = Join-Path $caseFolder 'video.mp4'
    $stderr = Join-Path $OutputFolder $(if ($collision) { 'publication-error.txt' } else { 'audio-warning.txt' })
    $arguments = @('--selftest-cli-safety', $caseFolder, 'recording-audio-failure', 'record',
        '--rect', '2,2,320,240', '--seconds', '2', '--format', 'mp4', '--sound', 'microphone', '--out', $destination)
    $quotedArguments = $arguments | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath $AppPath -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru -RedirectStandardError $stderr
    if ($collision) {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $stage = @(Get-ChildItem -LiteralPath $caseFolder -Filter '.KillerShot-staging-*' -Directory -Force -ErrorAction SilentlyContinue)
            if ($stage.Count) { break }
            if ($process.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Recording never reached staging.' }
            Start-Sleep -Milliseconds 25
        } while ($true)
        [System.IO.File]::WriteAllText($destination, 'Preserve this concurrent output.')
    }
    if (-not $process.WaitForExit(20000)) { throw 'CLI recording did not finish within 20 seconds.' }
    $errorText = Get-Content -LiteralPath $stderr -Raw
    if ($collision) {
        $retained = @(Get-ChildItem -LiteralPath $caseFolder -Recurse -Force -Filter 'recording.mp4')
        if ($process.ExitCode -ne 1 -or $retained.Count -ne 1 -or $retained[0].Length -le 0 -or
            [System.IO.File]::ReadAllText($destination) -ne 'Preserve this concurrent output.' -or $errorText -notmatch 'It remains at') {
            throw "Failed publication lost finalized output or replaced the concurrent file: $errorText"
        }
    } elseif ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $destination) -or
        (Get-Item -LiteralPath $destination).Length -le 0 -or $errorText -notmatch 'saved with a warning.*Synthetic recording audio failure') {
        throw "Recoverable audio failure lost the recording: $errorText"
    }
    Write-Output "PASS: CLI recording publication collision=$collision"
    $process.Dispose()
}
