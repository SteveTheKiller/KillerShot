param([Parameter(Mandatory=$true)][string]$AppPath)
$ErrorActionPreference = 'Stop'
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Resolve-Path -LiteralPath $AppPath).Path
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$version = (Select-Xml -Path (Join-Path $PSScriptRoot '..\KillerShot.csproj') -XPath '/Project/PropertyGroup/Version').Node.InnerText
$expectedVersion = '^' + [regex]::Escape($version)
foreach ($case in @(
    @{ Args = '--help'; Expected = 'qr <image'; Exit = 0 },
    @{ Args = 'help'; Expected = 'capture \[options\]'; Exit = 0 },
    @{ Args = '--cli help'; Expected = 'capture \[options\]'; Exit = 0 },
    @{ Args = 'version'; Expected = $expectedVersion; Exit = 0 },
    @{ Args = '--cli version'; Expected = $expectedVersion; Exit = 0 },
    @{ Args = 'qr "C:\this-qr-fixture-does-not-exist\code.png" --json'; Expected = 'image file does not exist'; Exit = 1 }
)) {
$start.Arguments = $case.Args
$process = [System.Diagnostics.Process]::Start($start)
try {
    $output = $process.StandardOutput.ReadToEndAsync()
    $errorOutput = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(5000)) {
        $process.Kill()
        throw 'The help command opened the app instead of returning help.'
    }
    $text = $output.GetAwaiter().GetResult()
    $errorText = $errorOutput.GetAwaiter().GetResult()
    if ($process.ExitCode -ne $case.Exit -or ($text + $errorText) -notmatch $case.Expected) {
        throw "Command '$($case.Args)' failed. Exit: $($process.ExitCode). Output: $text. Error: $errorText"
    }
    Write-Output "PASS: killershot $($case.Args)"
}
finally { $process.Dispose() }
}
