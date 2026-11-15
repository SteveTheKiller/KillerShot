param([string]$Root = (Split-Path -Parent $PSScriptRoot))
# Steve's rule: no shadow anywhere on a button, under the face, behind the caption or behind an
# icon. KillerUI checks its own Controls.xaml; this covers every XAML file in the app, so a button
# style, template or inline button in a window cannot carry a DropShadowEffect, BlurEffect or
# TextStroke either. Reads the XAML only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Xml.Linq
$x = [System.Xml.Linq.XNamespace]::Get('http://schemas.microsoft.com/winfx/2006/xaml')
$buttons = 'Button', 'ToggleButton', 'RadioButton', 'CheckBox', 'RepeatButton'
# Photo cards are Buttons for click and keyboard handling, but they are cards, and card shadows
# stay under the rule. Each exemption names the style key and why.
$cards = @{ 'LibTile' = 'grid and timeline photo card' }
function Test-Shadow($element) {
    @($element.DescendantsAndSelf() | Where-Object {
        $_.Name.LocalName -in 'DropShadowEffect', 'BlurEffect' -or
        @($_.Attributes() | Where-Object { $_.Value -match 'TextStroke|DropShadow' }).Count -gt 0
    }).Count -gt 0
}
$files = Get-ChildItem -LiteralPath $Root -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|Strings|shot-landing)\\' }
if ($files.Count -eq 0) { throw "No XAML found under $Root." }
$found = foreach ($file in $files) {
    $xaml = [System.Xml.Linq.XDocument]::Load($file.FullName, [System.Xml.Linq.LoadOptions]::SetLineInfo)
    foreach ($element in $xaml.Descendants()) {
        $local = $element.Name.LocalName
        $target = ''
        $card = @($element.AncestorsAndSelf() | Where-Object { $_.Name.LocalName -eq 'Style' -and $_.Attribute($x + 'Key') -and $cards.ContainsKey($_.Attribute($x + 'Key').Value) })
        if ($card.Count -gt 0) { continue }
        if ($local -in 'Style', 'ControlTemplate') {
            $attribute = $element.Attribute('TargetType')
            if ($attribute) { $target = $attribute.Value.Split(':')[-1].Trim('{', '}').Replace('x:Type ', '').Split(':')[-1] }
            if ($target -notin $buttons) { continue }
        }
        elseif ($local -notin $buttons) { continue }
        if (Test-Shadow $element) {
            $name = $element.Attribute($x + 'Key'); $label = if ($name) { $name.Value } else { "$local $target".Trim() }
            '{0}:{1} {2}' -f $file.FullName.Substring($Root.Length + 1), ([System.Xml.IXmlLineInfo]$element).LineNumber, $label
        }
    }
}
$found = @($found | Select-Object -Unique)
if ($found.Count -ne 0) { throw ("Buttons carry a shadow:`n  " + ($found -join "`n  ")) }
Write-Output "PASS: no button in $($files.Count) XAML files carries a shadow."
