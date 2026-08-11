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

# Make gradient direction explicit to avoid the .NET 8 overload ambiguity.
$text = $text.Replace('Color.FromArgb(0, 224, 255), 0);', 'Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal);')
$text = $text.Replace('Color.FromArgb(181, 70, 255), Color.FromArgb(0, 224, 255), 0);', 'Color.FromArgb(181, 70, 255), Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal);')

# Animated controls are hosted inside GRL cards; WinForms does not allow
# transparent BackColor on these custom controls, so use the card surface.
$text = $text.Replace('radar.BackColor = Color.Transparent;', 'radar.BackColor = Surface;')
$text = $text.Replace('graph.BackColor = Color.Transparent;', 'graph.BackColor = Surface;')

Set-Content -Path $path -Value $text -Encoding UTF8

$final = Get-Content $path -Raw
if ($final -notmatch 'public sealed partial class DashboardForm : Form') { throw 'Partial DashboardForm declaration was not applied.' }
if ($final -notmatch 'ApplyReferenceLayout\(\);') { throw 'Reference layout hook was not applied.' }
if ($final -match 'Color.FromArgb\(0, 224, 255\), 0\);') { throw 'Ambiguous gradient constructor remains.' }
if ($final -match 'radar\.BackColor = Color\.Transparent;|graph\.BackColor = Color\.Transparent;') { throw 'Unsupported transparent dashboard background remains.' }
Write-Host 'UI patch verification passed.'
