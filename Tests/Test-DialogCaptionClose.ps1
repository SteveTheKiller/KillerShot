param([string]$Root = (Split-Path -Parent $PSScriptRoot))
# Win98 caption rule: a caption button is (caption height - 4) tall and (caption height - 2) wide,
# centered in the band. Every ChromeCloseButton except the main window's CloseBtn sits in a dialog
# or card caption band, so it must bind DialogCloseWidth/DialogCloseHeight and center vertically.
# ThemeManager must derive those keys from DialogTitleBarHeight, and ChromeButton must take its
# glyph size from CaptionGlyphSize. Reads source only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Xml.Linq
$x = [System.Xml.Linq.XNamespace]::Get('http://schemas.microsoft.com/winfx/2006/xaml')
$failures = [System.Collections.Generic.List[string]]::new()
$files = Get-ChildItem -LiteralPath $Root -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|Strings|shot-landing|\.claude)\\' }
$count = 0
foreach ($file in $files) {
    $xaml = [System.Xml.Linq.XDocument]::Load($file.FullName, [System.Xml.Linq.LoadOptions]::SetLineInfo)
    foreach ($b in $xaml.Descendants() | Where-Object { $_.Name.LocalName -eq 'Button' }) {
        $style = $b.Attribute('Style')
        if (-not $style -or $style.Value -notmatch '\bChromeCloseButton\}') { continue }
        $name = $b.Attribute($x + 'Name')
        if ($name -and $name.Value -eq 'CloseBtn') { continue }
        $count++
        $where = '{0}:{1}' -f $file.FullName.Substring($Root.Length + 1), ([System.Xml.IXmlLineInfo]$b).LineNumber
        $w = $b.Attribute('Width'); $h = $b.Attribute('Height'); $v = $b.Attribute('VerticalAlignment')
        if (-not $w -or $w.Value -ne '{DynamicResource DialogCloseWidth}') { $failures.Add("$where Width is not DialogCloseWidth") }
        if (-not $h -or $h.Value -ne '{DynamicResource DialogCloseHeight}') { $failures.Add("$where Height is not DialogCloseHeight") }
        if (-not $v -or $v.Value -ne 'Center') { $failures.Add("$where VerticalAlignment is not Center") }
    }
}
if ($count -eq 0) { $failures.Add('No dialog ChromeCloseButton found.') }
$tm = Get-Content -Raw -LiteralPath (Join-Path $Root 'Services\ThemeManager.cs')
if ($tm -notmatch 'SetIfAbsent\("DialogCloseWidth",\s*pickerCaption \? bar - 2') { $failures.Add('ThemeManager: DialogCloseWidth is not DialogTitleBarHeight - 2') }
if ($tm -notmatch 'SetIfAbsent\("DialogCloseHeight",\s*pickerCaption \? bar - 4') { $failures.Add('ThemeManager: DialogCloseHeight is not DialogTitleBarHeight - 4') }
if ($tm -notmatch 'combined\["CaptionGlyphSize"\]\s*=\s*flat \? 8\.0 : 10\.0') { $failures.Add('ThemeManager: CaptionGlyphSize is not set') }
$controls = Get-Content -Raw -LiteralPath (Join-Path $Root 'Controls\Controls.xaml')
if ($controls -notmatch '(?s)x:Key="ChromeButton".*?Property="FontSize" Value="\{DynamicResource CaptionGlyphSize\}"') { $failures.Add('Controls.xaml: ChromeButton FontSize is not CaptionGlyphSize') }
if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "FAIL $_" }; exit 1 }
Write-Host "PASS $count dialog caption closes bind DialogCloseWidth/Height; kit derives them from DialogTitleBarHeight."
