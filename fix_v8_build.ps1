$ErrorActionPreference = 'Stop'

$p = 'GameRouteLabV8Form.cs'
$s = Get-Content $p -Raw

# Keep the palette field distinct from Control.Text and repair any older bad rewrite.
$s = $s -replace 'static readonly Color V8Colors\.TextColor\s*=', 'static readonly Color TextColor ='
$s = $s -replace 'static readonly Color Text\s*=', 'static readonly Color TextColor ='
$s = $s -replace '\bV8Colors\.TextColor\b', 'TextColor'
$s = $s -replace 'ForeColor\s*=\s*Text\b', 'ForeColor = TextColor'
# The custom header controls are outside GameRouteLabV8Form and use the shared palette class.
$s = $s -replace 'new SolidBrush\(TextColor\)', 'new SolidBrush(V8Colors.TextColor)'

# These labels are intentionally reassigned when the center dashboard is built,
# so they must not be readonly fields.
$s = $s -replace 'readonly Label gameTitle = new\(\), gameDetails = new\(\), endpointTitle = new\(\), metricLabel = new\(\), qualityLabel = new\(\), networkLabel = new\(\), routerLabel = new\(\), guideLabel = new\(\), statusLabel = new\(\);', 'Label gameTitle = new(), gameDetails = new(), endpointTitle = new(), metricLabel = new(), qualityLabel = new(), networkLabel = new(), routerLabel = new(), guideLabel = new(), statusLabel = new();'

Set-Content $p $s -Encoding UTF8
Write-Host 'v8 source normalization applied.'
