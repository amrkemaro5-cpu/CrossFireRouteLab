$ErrorActionPreference = 'Stop'
$path = 'DashboardForm.cs'
$text = Get-Content $path -Raw

if ($text -notmatch 'public sealed partial class DashboardForm : Form') {
    $old = 'public sealed class DashboardForm : Form'
    if (-not $text.Contains($old)) { throw 'DashboardForm declaration not found.' }
    $text = $text.Replace($old, 'public sealed partial class DashboardForm : Form')
}

if ($text -notmatch 'ApplyReferenceLayout\(\);') {
    $old = "BuildUi();$([Environment]::NewLine)        RefreshMemory();"
    if (-not $text.Contains($old)) { $old = "BuildUi();`n        RefreshMemory();" }
    if (-not $text.Contains($old)) { throw 'DashboardForm initialization anchor not found.' }
    $new = "BuildUi();$([Environment]::NewLine)        ApplyReferenceLayout();$([Environment]::NewLine)        RefreshMemory();"
    $text = $text.Replace($old, $new)
}

# System.Drawing has two LinearGradientBrush constructors whose final argument can
# accept a numeric zero. Make the intended direction explicit so Release builds are
# deterministic and never fail with CS0121.
$text = $text.Replace(
    'Color.FromArgb(0, 224, 255), 0);',
    'Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal);')
$text = $text.Replace(
    'Color.FromArgb(181, 70, 255), Color.FromArgb(0, 224, 255), 0);',
    'Color.FromArgb(181, 70, 255), Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal);')

Set-Content -Path $path -Value $text -Encoding UTF8

$final = Get-Content $path -Raw
if ($final -notmatch 'public sealed partial class DashboardForm : Form') { throw 'Partial DashboardForm declaration was not applied.' }
if ($final -notmatch 'ApplyReferenceLayout\(\);') { throw 'Reference layout hook was not applied.' }
if ($final -match 'public AnimatedRadar\(\) \{[^}]*BackColor = Color\.Transparent;') { throw 'A transparent AnimatedRadar constructor remains; refusing to build.' }
if ($final -match 'Color.FromArgb\(0, 224, 255\), 0\);') { throw 'Ambiguous gradient constructor remains; refusing to build.' }
Write-Host 'Reference UI patch and gradient constructor verification passed.'
