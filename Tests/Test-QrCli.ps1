param(
    [Parameter(Mandatory = $true)][string]$Image,
    [string]$App,
    [switch]$BareCommand
)
$ErrorActionPreference = 'Stop'
if (!$App) { $App = Join-Path $PSScriptRoot '..\bin\Debug\net10.0-windows10.0.26100.0\KillerShot.exe' }
if (!(Test-Path -LiteralPath $Image -PathType Leaf)) { throw 'The QR test image is missing' }
if (!(Test-Path -LiteralPath $App -PathType Leaf)) { throw 'Build KillerShot before the QR CLI check' }
$arguments = @('qr', $Image, '--json')
if (!$BareCommand) { $arguments = @('--cli') + $arguments }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = [IO.Path]::GetFullPath($App)
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
$output = $process.StandardOutput.ReadToEndAsync()
$errors = $process.StandardError.ReadToEndAsync()
if (!$process.WaitForExit(60000)) { throw 'QR command did not finish within 60 seconds' }
if ($process.ExitCode -ne 0) { throw "QR command failed with exit code $($process.ExitCode): $($errors.Result)" }
$result = $output.Result | ConvertFrom-Json
if (@($result.codes).Count -ne 1 -or $result.codes[0].text -ne 'https://killershot.net/qr') { throw 'QR command returned the wrong payload' }
if ($result.codes[0].link.disposition -ne 'Allowed') { throw 'QR command did not run the local link checks' }
Write-Output 'PASS: QR file command returns the expected payload and local link assessment.'
