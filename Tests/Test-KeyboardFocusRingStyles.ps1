param([string]$Root = (Split-Path -Parent $PSScriptRoot))
# A style that sets FocusVisualStyle to null switches the family keyboard ring off for every control
# that uses it (the rail, the folder tree, check rows, toolbar buttons and the danger close button)
# once the style is applied again, as it is on a theme change. WPF only draws FocusVisualStyle for
# keyboard focus, so leaving it to the family ring keeps clicks ring-free. Reads the XAML only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Xml.Linq
$path = Join-Path $Root 'Controls\Controls.xaml'
if (-not (Test-Path -LiteralPath $path)) { throw "Controls.xaml not found under $Root." }
$xaml = [System.Xml.Linq.XDocument]::Load($path)
$x = [System.Xml.Linq.XNamespace]::Get('http://schemas.microsoft.com/winfx/2006/xaml')
$off = @(foreach ($setter in $xaml.Descendants()) {
    if ($setter.Name.LocalName -ne 'Setter') { continue }
    $property = $setter.Attribute('Property'); $value = $setter.Attribute('Value')
    if ($null -eq $property -or $null -eq $value) { continue }
    if ($property.Value -ne 'FocusVisualStyle' -or $value.Value -ne '{x:Null}') { continue }
    $key = $setter.Parent.Attribute($x + 'Key')
    if ($null -eq $key) { '(implicit)' } else { $key.Value }
})
if ($off.Count -ne 0) { throw ('Styles in Controls.xaml switch the keyboard focus ring off: ' + ($off -join ', ') + '.') }
Write-Output 'PASS: no shared style switches the keyboard focus ring off.'
