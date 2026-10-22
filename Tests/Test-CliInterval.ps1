param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$Output
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Output) { throw 'Use a new scratch output folder for each test.' }
[IO.Directory]::CreateDirectory($Output) | Out-Null
function Invoke-IntervalCase([string]$Name, [string[]]$Options, [int]$ExpectedExit, [string]$ExpectedError) {
    $caseRoot = Join-Path $Output $Name
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($App))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in (@('--selftest-cli-safety', $caseRoot, 'account', 'capture') + $Options)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { $process.Kill($true); throw 'The interval command did not terminate.' }
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $caseRoot 'stdout.txt'), $text)
        [IO.File]::WriteAllText((Join-Path $caseRoot 'stderr.txt'), $errorText)
        if ($process.ExitCode -ne $ExpectedExit -or ($ExpectedError -and $errorText -notmatch $ExpectedError)) {
            throw "$Name failed: exit=$($process.ExitCode), expected=$ExpectedExit, stderr=$($errorText.Substring(0, [Math]::Min(512, $errorText.Length)))"
        }
        Write-Output "PASS: $Name"
    } finally { $process.Dispose() }
}
Invoke-IntervalCase 'inline-no-output' @('--interval=1', '--count=2', '--no-save', '--no-clipboard', '--no-recent') 2 'needs.*output'
Invoke-IntervalCase 'spaced-no-output' @('--interval', '1', '--count', '2', '--no-save', '--no-clipboard', '--no-recent') 2 'needs.*output'
foreach ($case in @(
    @{ Name = 'spaced'; Options = @('--interval', '1', '--count', '2') },
    @{ Name = 'inline'; Options = @('--interval=1', '--count=2') },
    @{ Name = 'mixed-case'; Options = @('--InTeRvAl', '1', '--CoUnT', '2') },
    @{ Name = 'inline-interval'; Options = @('--INTERVAL=1', '--CoUnT', '2') },
    @{ Name = 'inline-count'; Options = @('--INTERVAL', '1', '--count=2') }
)) {
    $images = Join-Path $Output ($case.Name + '-images')
    [IO.Directory]::CreateDirectory($images) | Out-Null
    $single = @('--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--out', $images, '--no-clipboard', '--no-recent', '--json')
    Invoke-IntervalCase $case.Name ($case.Options + $single) 0 ''
    $lines = @(Get-Content -LiteralPath (Join-Path $Output ($case.Name + '\stdout.txt')) | Where-Object { $_.Trim() })
    $files = @(Get-ChildItem -LiteralPath $images -Filter '*.png' -File)
    if ($lines.Count -ne 2 -or $files.Count -ne 2) { throw ($case.Name + ' did not produce exactly two captures.') }
    $paths = @()
    foreach ($line in $lines) {
        $result = $line | ConvertFrom-Json
        $paths += $result.path
        if ($result.width -ne 2 -or $result.height -ne 2 -or $result.clipboard -or $null -ne $result.libraryId) { throw 'The repeated capture changed its requested outputs.' }
        $bytes = [IO.File]::ReadAllBytes($result.path)
        if ($bytes.Length -lt 24 -or [Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A' -or [Convert]::ToHexString($bytes[16..23]) -ne '0000000200000002') { throw 'A repeated output is not the requested 2x2 PNG.' }
    }
    if (@($paths | Select-Object -Unique).Count -ne 2) { throw 'Repeated capture reused one output path.' }
    Write-Output ('PASS: ' + $case.Name + ' produced exactly two independent PNG outputs')
}
foreach ($case in @(
    @{ Name = 'missing-count'; Options = @('--interval=1'); Error = 'needs --count' },
    @{ Name = 'count-only'; Options = @('--count=2'); Error = 'only used with --interval' },
    @{ Name = 'zero-count'; Options = @('--interval=1', '--count=0'); Error = 'whole number' },
    @{ Name = 'duplicate-count'; Options = @('--interval=1', '--count=2', '--COUNT', '2'); Error = 'more than once' },
    @{ Name = 'interactive-repeat'; Options = @('--interval=1', '--count=2', '--pick'); Error = 'cannot be combined' }
)) {
    Invoke-IntervalCase $case.Name $case.Options 2 $case.Error
    if ((Get-Content -LiteralPath (Join-Path $Output ($case.Name + '\stdout.txt')) -Raw)) { throw 'Invalid repeat options produced capture output.' }
}
