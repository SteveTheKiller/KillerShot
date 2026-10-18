param([string]$SourceRoot = (Join-Path $PSScriptRoot '..\..\KillerTools\src\KillerTools.Qr'))
$ErrorActionPreference = 'Stop'
$copyRoot = Join-Path $PSScriptRoot '..\Qr'
if (!(Test-Path -LiteralPath $SourceRoot -PathType Container)) { throw "QR reference is unavailable: $SourceRoot" }
if (!(Test-Path -LiteralPath $copyRoot -PathType Container)) { throw "QR copy is missing: $copyRoot" }
$sourceFiles = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.cs' -File | Sort-Object Name)
$copyFiles = @(Get-ChildItem -LiteralPath $copyRoot -Filter '*.cs' -File | Sort-Object Name)
if ($sourceFiles.Count -eq 0) { throw 'QR reference contains no source files' }
if (Compare-Object @($sourceFiles.Name) @($copyFiles.Name)) { throw 'QR source file sets differ' }
foreach ($file in $sourceFiles) {
    $copyPath = Join-Path $copyRoot $file.Name
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copyPath).Hash) {
        throw "QR copy drift: $($file.Name)"
    }
}
Write-Output "PASS: $($sourceFiles.Count) QR source files match KillerTools byte for byte."
