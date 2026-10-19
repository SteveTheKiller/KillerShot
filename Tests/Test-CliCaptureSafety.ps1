param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$Output,
    [ValidateSet('all', 'no-output', 'protected', 'failed-write', 'saved-file', 'account')][string]$Case = 'all'
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { throw 'Use a new scratch output folder for each test.' }
[IO.Directory]::CreateDirectory($Output) | Out-Null
function Invoke-CaptureCase([string]$Name, [string]$Fixture, [string[]]$Arguments, [int]$ExpectedExit, [string]$ExpectedError) {
    $caseRoot = Join-Path $Output $Name
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($App))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in (@('--selftest-cli-safety', $caseRoot, $Fixture) + $Arguments)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { $process.Kill($true); throw 'The CLI check timed out.' }
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $caseRoot 'stdout.txt'), $text)
        [IO.File]::WriteAllText((Join-Path $caseRoot 'stderr.txt'), $errorText)
        if ($process.ExitCode -ne $ExpectedExit -or ($ExpectedError -and $errorText -notmatch $ExpectedError)) {
            throw "$Name failed: exit=$($process.ExitCode), stdout=$text, stderr=$errorText"
        }
        Write-Output "PASS: $Name"
    } finally { $process.Dispose() }
}
if ($Case -in @('all', 'no-output')) {
    Invoke-CaptureCase 'no-output' 'account' @('capture', '--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--no-save', '--no-clipboard', '--no-recent') 2 'needs.*output'
}
if ($Case -in @('all', 'protected')) {
    Invoke-CaptureCase 'protected-only' 'protected' @('capture', '--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--no-save', '--no-clipboard') 1 'command line cannot open password-protected libraries'
    Invoke-CaptureCase 'protected-preflight' 'protected' @('capture', '--mode', 'region', '--rect', '1000000,1000000,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--no-save', '--no-clipboard') 1 'command line cannot open password-protected libraries'
    Invoke-CaptureCase 'protected-recent' 'protected' @('recent', '--json') 1 'command line cannot open password-protected libraries'
}
if ($Case -in @('all', 'failed-write')) {
    Invoke-CaptureCase 'failed-write-only' 'failed-write' @('capture', '--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--no-save', '--no-clipboard', '--json') 1 'synthetic write failure'
}
if ($Case -in @('all', 'saved-file')) {
    $saved = Join-Path $Output 'protected-output.png'
    Invoke-CaptureCase 'protected-with-file' 'protected' @('capture', '--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--out', $saved, '--no-clipboard', '--json') 0 'command line cannot open password-protected libraries'
    $result = Get-Content -LiteralPath (Join-Path $Output 'protected-with-file\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.path -ne $saved -or $null -ne $result.libraryId -or !$result.width -or $result.clipboard -or !(Test-Path -LiteralPath $saved -PathType Leaf)) { throw 'A successful partial capture did not return its saved output.' }
    $bytes = [IO.File]::ReadAllBytes($saved)
    if ($bytes.Length -lt 24 -or [Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A' -or [Convert]::ToHexString($bytes[16..23]) -ne '0000000200000002') { throw 'The saved output is not the expected 2x2 PNG.' }
    Write-Output 'PASS: saved partial output exists and has the requested dimensions'
}
if ($Case -in @('all', 'account')) {
    Invoke-CaptureCase 'account-only' 'account' @('capture', '--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--no-save', '--no-clipboard', '--json') 0 ''
    $result = Get-Content -LiteralPath (Join-Path $Output 'account-only\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.libraryId -lt 1 -or $null -ne $result.path -or $result.clipboard) { throw 'A successful album-only capture did not return its saved capture ID.' }
}
