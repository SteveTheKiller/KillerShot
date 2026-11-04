param([Parameter(Mandatory=$true)][string]$AppPath)
# Command line edge cases: edit --out must take a folder or refuse an unsupported type as a usage
# error, and a recognition language typed in another case must be stored as the installed tag.
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppPath).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('KillerShot-CliOutputs-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Invoke-Cli([string]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($app, $Arguments)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw "killershot $Arguments did not finish." }
        [pscustomobject]@{ Exit = $process.ExitCode; Out = $out.GetAwaiter().GetResult(); Err = $err.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

try {
    Add-Type -AssemblyName PresentationCore
    $pixels = [byte[]]::new(16 * 16 * 4)
    $bitmap = [Windows.Media.Imaging.BitmapSource]::Create(16, 16, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $pixels, 64)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $source = Join-Path $work 'source.png'
    $stream = [IO.File]::Create($source)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }

    $bmp = Invoke-Cli "edit `"$source`" --margin 2 --out `"$(Join-Path $work 'y.bmp')`""
    if ($bmp.Exit -ne 2) { throw "edit --out y.bmp exited $($bmp.Exit), expected usage error 2. $($bmp.Err)" }
    Write-Output 'PASS: edit --out with an unsupported type is a usage error'

    $folder = Join-Path $work 'out'
    New-Item -ItemType Directory -Path $folder | Out-Null
    $toFolder = Invoke-Cli "edit `"$source`" --margin 2 --out `"$folder`""
    if ($toFolder.Exit -ne 0 -or @(Get-ChildItem -LiteralPath $folder -File).Count -ne 1) {
        throw "edit --out <folder> exited $($toFolder.Exit) with $(@(Get-ChildItem -LiteralPath $folder -File).Count) files. $($toFolder.Err)"
    }
    Write-Output 'PASS: edit --out with a folder saves one file inside it'

    # The README's way to read an exit code from a script.
    $run = Start-Process $app -ArgumentList '--cli', 'qr', (Join-Path $work 'missing.png'), '--json' -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $work 'qr.json') -RedirectStandardError (Join-Path $work 'qr.err')
    if ($run.ExitCode -ne 1) { throw "The README's Start-Process -Wait -PassThru pattern read exit code $($run.ExitCode), expected 1." }
    Write-Output 'PASS: the documented Start-Process pattern reads the command exit code'

    $languages = (Invoke-Cli 'text --languages --json').Out | ConvertFrom-Json
    $tag = @($languages.languages)[0].tag
    if ($tag) {
        $before = $languages.chosen
        $mixed = -join ($tag.ToCharArray() | ForEach-Object -Begin { $i = 0 } -Process { if ($i++ % 2) { "$_".ToUpperInvariant() } else { "$_".ToLowerInvariant() } })
        try {
            $set = Invoke-Cli "settings --set text.language=$mixed"
            if ($set.Exit -ne 0) { throw "settings --set text.language=$mixed exited $($set.Exit). $($set.Err)" }
            $after = (Invoke-Cli 'text --languages --json').Out | ConvertFrom-Json
            if ($after.chosen -cne $tag) { throw "A language typed as $mixed was stored as '$($after.chosen)', not the installed tag $tag." }
            Write-Output "PASS: a language typed as $mixed is stored as $tag"
        }
        finally { Invoke-Cli ('settings --set text.language=' + $(if ($before) { $before } else { 'windows' })) | Out-Null }
    }
    else { Write-Output 'SKIP: no recognition language is installed' }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
