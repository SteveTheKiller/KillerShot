param([Parameter(Mandatory=$true)][string]$AppPath)
# The headless self-test switch: a hexadecimal --window handle is honored, an unreadable one is a
# usage error, and a missing folder is never taken from the next switch.
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppPath).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('KillerShot-SelfTestArgs-' + [guid]::NewGuid().ToString('N'))
Add-Type -Namespace KillerShotTest -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern System.IntPtr GetDesktopWindow();'
function Run([string[]]$Arguments) {
    $p = Start-Process $app -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    $p.ExitCode
}
try {
    $hex = '0x{0:X}' -f [KillerShotTest.User32]::GetDesktopWindow().ToInt64()
    $folder = Join-Path $work 'hex'
    $exit = Run @('--selftest', $folder, '--window', $hex)
    $report = Get-Content -Raw -LiteralPath (Join-Path $folder 'report.txt')
    if ($report -notmatch 'wgc_supported=') { throw "--window $hex was ignored (exit $exit)." }
    Write-Output 'PASS: --window takes a hexadecimal handle'

    $exit = Run @('--selftest', (Join-Path $work 'bad'), '--window', 'nonsense')
    if ($exit -ne 2) { throw "--window nonsense exited $exit, expected usage error 2." }
    Write-Output 'PASS: an unreadable --window handle is a usage error'

    Push-Location $work
    try {
        $exit = Run @('--selftest', '--window', $hex)
        if (Test-Path -LiteralPath (Join-Path $work '--window')) { throw '--selftest took the next switch as its folder.' }
        if ($exit -ne 2) { throw "--selftest followed by a switch exited $exit, expected usage error 2." }
    }
    finally { Pop-Location }
    Write-Output 'PASS: --selftest never takes the next switch as its folder'
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
