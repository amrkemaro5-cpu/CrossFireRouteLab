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

Set-Content -Path $path -Value $text -Encoding UTF8

$final = Get-Content $path -Raw
if ($final -notmatch 'public sealed partial class DashboardForm : Form') { throw 'Partial DashboardForm declaration was not applied.' }
if ($final -notmatch 'ApplyReferenceLayout\(\);') { throw 'Reference layout hook was not applied.' }
if ($final -match 'public AnimatedRadar\(\) \{[^}]*BackColor = Color\.Transparent;') { throw 'A transparent AnimatedRadar constructor remains; refusing to build.' }
Write-Host 'Reference UI patch verified.'
