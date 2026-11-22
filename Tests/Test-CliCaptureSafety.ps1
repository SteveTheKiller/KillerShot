param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$Output,
    [ValidateSet('all', 'no-output', 'protected', 'failed-write', 'saved-file', 'account', 'option-value', 'redact', 'recent-id', 'recent-recording', 'record-copy')][string]$Case = 'all'
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
if ($Case -in @('all', 'option-value')) {
    # "--out --x.png" is a missing value followed by another option, not a file named --x.png
    # in the working folder.
    Invoke-CaptureCase 'option-value' 'account' @('capture', '--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--no-clipboard', '--out', '--x.png') 2 '--out needs a value'
    if (Test-Path -LiteralPath (Join-Path (Get-Location) '--x.png')) { throw 'The capture wrote a file named --x.png.' }
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
if ($Case -in @('all', 'redact')) {
    # Scan every capture covers CLI captures too: the stand-in reader finds an email at 4,4 24x12
    # and the box is filled magenta, a color the screen under the capture never has.
    Add-Type -AssemblyName PresentationCore
    function Get-Pixel([string]$Path, [int]$X, [int]$Y) {
        $frame = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new($Path), [Windows.Media.Imaging.BitmapCreateOptions]::None, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $bgra = [Windows.Media.Imaging.FormatConvertedBitmap]::new($frame, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
        $px = [byte[]]::new(4)
        $bgra.CopyPixels([Windows.Int32Rect]::new($X, $Y, 1, 1), $px, 4, 0)
        '{0:X2}{1:X2}{2:X2}' -f $px[2], $px[1], $px[0]
    }
    $redacted = Join-Path $Output 'redacted.png'
    Invoke-CaptureCase 'redact-found' 'redact' @('capture', '--mode', 'region', '--rect', '16,16,32,24', '--delay', '0', '--no-shutter', '--no-cursor', '--no-clipboard', '--out', $redacted, '--json') 0 ''
    $result = Get-Content -LiteralPath (Join-Path $Output 'redact-found\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.redactionChecked -ne $true -or $result.redactions -ne 1) { throw "A CLI capture with Scan every capture on did not report its redaction: $($result | ConvertTo-Json -Compress)" }
    foreach ($point in @(@(8, 8), @(16, 10), @(26, 14))) {
        $color = Get-Pixel $redacted $point[0] $point[1]
        if ($color -ne 'FF00FF') { throw "The saved CLI capture shows $color at $($point -join ',') where the found email should be covered." }
    }
    Write-Output 'PASS: CLI capture is redacted before it is saved'
    $unchecked = Join-Path $Output 'unchecked.png'
    Invoke-CaptureCase 'redact-failed' 'redact-failed' @('capture', '--mode', 'region', '--rect', '16,16,32,24', '--delay', '0', '--no-shutter', '--no-cursor', '--no-clipboard', '--out', $unchecked, '--json') 0 'not checked for sensitive text'
    $result = Get-Content -LiteralPath (Join-Path $Output 'redact-failed\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.redactionChecked -ne $false -or $result.redactions -ne 0 -or !(Test-Path -LiteralPath $unchecked -PathType Leaf)) { throw "A CLI capture whose scan failed did not save the original and say so: $($result | ConvertTo-Json -Compress)" }
    if ((Get-Pixel $unchecked 16 10) -eq 'FF00FF') { throw 'A CLI capture whose scan failed was boxed anyway.' }
    Write-Output 'PASS: a failed scan saves the original and warns that it was not checked'
}
if ($Case -in @('all', 'recent-id')) {
    # recent actions take id:<id> from --json, so a script acts on the capture it listed even
    # after the list positions move. Each case runs in its own process and folder, so the
    # fixture album holds the two captures every time.
    Invoke-CaptureCase 'recent-list' 'two-captures' @('recent', '--json') 0 ''
    $listed = Get-Content -LiteralPath (Join-Path $Output 'recent-list\stdout.txt') -Raw | ConvertFrom-Json
    $older = $listed.items | Where-Object title -eq 'Older capture'
    if ($listed.total -ne 2 -or $older.index -ne 2) { throw "The fixture album did not list two captures, older second: $($listed | ConvertTo-Json -Compress -Depth 4)" }
    Invoke-CaptureCase 'recent-note-id' 'two-captures' @('recent', '--note', "id:$($older.id)", '--text', 'picked by id', '--json') 0 ''
    $result = Get-Content -LiteralPath (Join-Path $Output 'recent-note-id\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.id -ne $older.id -or $result.index -ne 2 -or $result.action -ne 'note') { throw "recent --note id: acted on the wrong capture: $($result | ConvertTo-Json -Compress)" }
    Invoke-CaptureCase 'recent-missing-id' 'two-captures' @('recent', '--delete', 'id:999999', '--yes') 1 'No capture has the id 999999'
    Invoke-CaptureCase 'recent-bad-id' 'two-captures' @('recent', '--open', 'id:abc') 2 'needs a capture id'
    Invoke-CaptureCase 'recent-id-search' 'two-captures' @('recent', '--copy', "id:$($older.id)", '--search', 'Older') 2 'does not go with --search'
    Write-Output 'PASS: recent actions pick captures by id'
}
if ($Case -in @('all', 'recent-recording')) {
    # A recording in the album exports as its clip, byte for byte, never as its poster frame.
    $clip = Join-Path $Output 'exported.mp4'
    Invoke-CaptureCase 'recording-export-file' 'recording' @('recent', '--export', '1', '--out', $clip, '--json') 0 ''
    $result = Get-Content -LiteralPath (Join-Path $Output 'recording-export-file\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.path -ne $clip -or [Convert]::ToHexString([IO.File]::ReadAllBytes($clip)) -ne '0103050709') { throw "recent --export did not write the recording's clip: $($result | ConvertTo-Json -Compress)" }
    $folder = Join-Path $Output 'clips'
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    Invoke-CaptureCase 'recording-export-folder' 'recording' @('recent', '--export', '1', '--out', $folder, '--json') 0 ''
    $files = @(Get-ChildItem -LiteralPath $folder -File)
    if ($files.Count -ne 1 -or $files[0].Extension -ne '.mp4' -or [Convert]::ToHexString([IO.File]::ReadAllBytes($files[0].FullName)) -ne '0103050709') { throw "recent --export into a folder did not leave exactly the clip: $($files.Name -join ', ')" }
    Invoke-CaptureCase 'recording-export-png' 'recording' @('recent', '--export', '1', '--out', (Join-Path $Output 'poster.png')) 2 'must end in \.mp4 for this recording'
    if (Test-Path -LiteralPath (Join-Path $Output 'poster.png')) { throw 'A recording was exported as a picture.' }
    Invoke-CaptureCase 'recording-copy' 'recording' @('recent', '--copy', '1') 1 'is a recording'
    Write-Output 'PASS: recordings export as their clip and are not copied as pictures'
}
if ($Case -in @('all', 'record-copy')) {
    # With Copy file to clipboard on, a recording copies without --clipboard, and
    # --no-clipboard still leaves the clipboard alone.
    $marker = Join-Path $Output 'record-follows\copied.txt'
    $clip = Join-Path $Output 'follows.gif'
    Invoke-CaptureCase 'record-follows' 'record-copy' @('record', '--mode', 'region', '--rect', '16,16,32,24', '--seconds', '1', '--format', 'gif', '--no-cursor', '--out', $clip, '--json') 0 ''
    $result = Get-Content -LiteralPath (Join-Path $Output 'record-follows\stdout.txt') -Raw | ConvertFrom-Json
    if (-not $result.clipboard -or -not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw) -ne $clip) { throw "record ignored Copy file to clipboard: $($result | ConvertTo-Json -Compress)" }
    $kept = Join-Path $Output 'kept.gif'
    Invoke-CaptureCase 'record-no-clipboard' 'record-copy' @('record', '--mode', 'region', '--rect', '16,16,32,24', '--seconds', '1', '--format', 'gif', '--no-cursor', '--no-clipboard', '--out', $kept, '--json') 0 ''
    $result = Get-Content -LiteralPath (Join-Path $Output 'record-no-clipboard\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.clipboard -or (Test-Path -LiteralPath (Join-Path $Output 'record-no-clipboard\copied.txt'))) { throw 'record --no-clipboard still copied the recording.' }
    Write-Output 'PASS: record follows Copy file to clipboard and --no-clipboard overrides it'
}
if ($Case -in @('all', 'account')) {
    Invoke-CaptureCase 'account-only' 'account' @('capture', '--mode', 'region', '--rect', '16,16,2,2', '--delay', '0', '--no-shutter', '--no-cursor', '--no-save', '--no-clipboard', '--json') 0 ''
    $result = Get-Content -LiteralPath (Join-Path $Output 'account-only\stdout.txt') -Raw | ConvertFrom-Json
    if ($result.libraryId -lt 1 -or $null -ne $result.path -or $result.clipboard) { throw 'A successful album-only capture did not return its saved capture ID.' }
}
