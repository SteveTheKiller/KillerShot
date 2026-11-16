param(
    [Parameter(Mandatory = $true)][string]$Image,
    [string]$App,
    [Parameter(Mandatory = $true)][string]$Work
)
# The QR command must admit an encoded image file without allocating its whole length. The
# CLI runs with a managed heap capped below the padding, so reading the file in full fails.
$ErrorActionPreference = 'Stop'
if (!$App) { $App = Join-Path $PSScriptRoot '..\KillerShot.Cli\bin\Debug\net10.0-windows\KillerShot.Cli.exe' }
if (!(Test-Path -LiteralPath $Image -PathType Leaf)) { throw 'The QR test image is missing' }
if (!(Test-Path -LiteralPath $App -PathType Leaf)) { throw 'Build KillerShot.Cli before the QR input check' }
if (Test-Path -LiteralPath $Work) { throw 'The work folder must be new' }
New-Item -ItemType Directory -Path $Work | Out-Null
$Work = (Resolve-Path -LiteralPath $Work).Path
$heapLimit = 192MB
$padding = 256MB
$failures = 0; $passes = 0

function Invoke-Qr([string]$path) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [IO.Path]::GetFullPath($App)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOTNET_GCHeapHardLimit'] = '0x{0:X}' -f $heapLimit
    foreach ($argument in @('qr', $path, '--json')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'QR command did not finish within 120 seconds' }
    [pscustomobject]@{ Exit = $process.ExitCode; Out = $output.Result; Err = $errors.Result.Trim() }
}
function Check([string]$name, [bool]$ok, $run) {
    if ($ok) { $script:passes++; Write-Output "PASS: $name" }
    else { $script:failures++; Write-Output "FAIL: $name (exit $($run.Exit), stderr: $($run.Err))" }
}
function Test-Payload($run) {
    if ($run.Exit -ne 0) { return $false }
    $result = $run.Out | ConvertFrom-Json
    @($result.codes).Count -eq 1 -and $result.codes[0].text -eq 'https://killershot.net/qr'
}
function New-Padded([string]$path, [long]$extra) {
    Copy-Item -LiteralPath $Image -Destination $path
    $stream = [IO.File]::Open($path, 'Open', 'ReadWrite')
    try { $stream.SetLength($stream.Length + $extra) } finally { $stream.Dispose() }
}

$plain = Invoke-Qr $Image
Check 'plain QR image decodes under the heap cap' (Test-Payload $plain) $plain

$padded = Join-Path $Work 'padded.png'
New-Padded $padded $padding
$run = Invoke-Qr $padded
Check 'QR image with 256 MB trailing data decodes without reading it all' (Test-Payload $run) $run

$huge = Join-Path $Work 'huge.png'
$stream = [IO.File]::Create($huge)
try { $stream.Write([byte[]](0x89, 0x50, 0x4E, 0x47), 0, 4); $stream.SetLength(1GB) } finally { $stream.Dispose() }
$run = Invoke-Qr $huge
Check 'a 1 GB malformed file is refused by size' ($run.Exit -eq 1 -and $run.Err -match 'larger than' -and $run.Err -notmatch 'memory') $run

$noCode = Join-Path $Work 'blank.png'
Add-Type -AssemblyName PresentationCore, WindowsBase
$pixels = [byte[]]::new(64 * 64 * 4); [Array]::Fill($pixels, [byte]255)
$bitmap = [Windows.Media.Imaging.BitmapSource]::Create(64, 64, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $pixels, 256)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [IO.File]::Create($noCode); try { $encoder.Save($stream) } finally { $stream.Dispose() }
$run = Invoke-Qr $noCode
Check 'an image without a code returns no codes' ($run.Exit -eq 0 -and @(($run.Out | ConvertFrom-Json).codes).Count -eq 0) $run

$garbage = Join-Path $Work 'garbage.png'
[IO.File]::WriteAllBytes($garbage, [byte[]](1..200 | ForEach-Object { [byte]($_ % 256) }))
$run = Invoke-Qr $garbage
Check 'a small malformed file fails cleanly' ($run.Exit -eq 1 -and $run.Err -ne '') $run

# A file that keeps growing while the command reads it must not be read past its admitted size.
$growing = Join-Path $Work 'growing.png'
Copy-Item -LiteralPath $Image -Destination $growing
$writer = Start-ThreadJob -ArgumentList $growing -ScriptBlock {
    param($path)
    $stream = [IO.File]::Open($path, 'Open', 'Write', 'ReadWrite'); $chunk = [byte[]]::new(16MB)
    try { for ($i = 0; $i -lt 48; $i++) { $stream.Seek(0, 'End') | Out-Null; $stream.Write($chunk, 0, $chunk.Length); $stream.Flush() } }
    finally { $stream.Dispose() }
}
Start-Sleep -Milliseconds 300
$run = Invoke-Qr $growing
$writer | Wait-Job | Receive-Job; $writer | Remove-Job
Check 'a growing file is decoded or refused without exhausting memory' (((Test-Payload $run) -or ($run.Exit -eq 1 -and $run.Err -match 'larger than')) -and $run.Err -notmatch 'memory') $run

Remove-Item -LiteralPath $Work -Recurse -Force
Write-Output "QR input bounds: $passes passed, $failures failed"
if ($failures) { exit 1 }
