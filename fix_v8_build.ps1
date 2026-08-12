$ErrorActionPreference = 'Stop'

$p = 'GameRouteLabV8Form.cs'
$s = Get-Content $p -Raw

# Keep the palette field distinct from Control.Text and repair any older bad rewrite.
$s = $s -replace 'static readonly Color V8Colors\.TextColor\s*=', 'static readonly Color TextColor ='
$s = $s -replace 'static readonly Color Text\s*=', 'static readonly Color TextColor ='
$s = $s -replace '\bV8Colors\.TextColor\b', 'TextColor'
$s = $s -replace '(?m)(ForeColor\s*=\s*)Text(\s*;)', '${1}TextColor$2'

# These labels are intentionally reassigned when the center dashboard is built,
# so they must not be readonly fields.
$s = $s -replace 'readonly Label gameTitle = new\(\), gameDetails = new\(\), endpointTitle = new\(\), metricLabel = new\(\), qualityLabel = new\(\), networkLabel = new\(\), routerLabel = new\(\), guideLabel = new\(\), statusLabel = new\(\);', 'Label gameTitle = new(), gameDetails = new(), endpointTitle = new(), metricLabel = new(), qualityLabel = new(), networkLabel = new(), routerLabel = new(), guideLabel = new(), statusLabel = new();'

Set-Content $p $s -Encoding UTF8
Write-Host 'v8 source normalization applied.'
