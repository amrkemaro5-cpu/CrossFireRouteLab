$ErrorActionPreference = 'Stop'
$path = 'DashboardForm.cs'
$text = Get-Content $path -Raw

if ($text -notmatch 'public sealed partial class DashboardForm : Form') {
    $old = 'public sealed class DashboardForm : Form'
    if (-not $text.Contains($old)) { throw 'DashboardForm declaration not found.' }
    $text = $text.Replace($old, 'public sealed partial class DashboardForm : Form')
}

if ($text -notmatch 'ApplyReferenceLayout\(\);') {
    $old = 'BuildUi();\n        RefreshMemory();'
    if (-not $text.Contains($old)) { throw 'DashboardForm initialization anchor not found.' }
    $text = $text.Replace($old, 'BuildUi();\n        ApplyReferenceLayout();\n        RefreshMemory();')
}

Set-Content -Path $path -Value $text -Encoding UTF8

if ((Get-Content $path -Raw) -notmatch 'public sealed partial class DashboardForm : Form') { throw 'Partial DashboardForm declaration was not applied.' }
if ((Get-Content $path -Raw) -notmatch 'ApplyReferenceLayout\(\);') { throw 'Reference layout hook was not applied.' }
if ((Get-Content $path -Raw) -match 'BackColor = Color\.Transparent; }') { throw 'A transparent custom-control constructor remains; refusing to build.' }
Write-Host 'Reference UI patch verified.'
