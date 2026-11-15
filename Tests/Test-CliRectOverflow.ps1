param([string]$App)
# SV-002: a --rect whose x + width passes int.MaxValue is a usage error (exit 2), not a crash
# (exit 1). Makes its own picture in a temporary folder and runs the window-free command line.
$ErrorActionPreference = 'Stop'
if (!$App) { $App = Join-Path $PSScriptRoot '..\bin\Debug\net10.0-windows\KillerShot.exe' }
if (!(Test-Path -LiteralPath $App -PathType Leaf)) { throw 'Build KillerShot before the rect overflow check' }
Add-Type -AssemblyName PresentationCore
$work = Join-Path ([IO.Path]::GetTempPath()) ('KillerShot-RectOverflow-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $image = Join-Path $work 'sample.png'
    $pixels = [byte[]]::new(64 * 64 * 4)
    $bitmap = [Windows.Media.Imaging.BitmapSource]::Create(64, 64, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $pixels, 64 * 4)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($image)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    $failures = 0
    foreach ($rect in @('2147483000,0,1000,10', '0,2147483000,10,1000')) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = [IO.Path]::GetFullPath($App)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('--cli', 'text', $image, '--rect', $rect)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        $null = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw "text --rect $rect did not finish within 60 seconds" }
        if ($process.ExitCode -ne 2) {
            Write-Output "FAIL: text --rect $rect exited $($process.ExitCode), expected 2: $($errors.Result.Trim())"
            $failures++
        }
        else { Write-Output "PASS: text --rect $rect is a usage error." }
    }
    if ($failures) { exit 1 }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
