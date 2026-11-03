# Rebuild the Windows icon and PNG previews from the geometry in Assets/glint.svg.
# Requires Windows and System.Drawing; no external graphics dependencies.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Glint.App\Assets'
$previewDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\icon-previews'
New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
$svgPath = Join-Path $assetDirectory 'glint.svg'
[xml]$svg = Get-Content -Raw -LiteralPath $svgPath
$namespace = New-Object System.Xml.XmlNamespaceManager($svg.NameTable)
$namespace.AddNamespace('s', 'http://www.w3.org/2000/svg')
$rectangle = $svg.SelectSingleNode('/s:svg/s:rect', $namespace)
$paths = $svg.SelectNodes('/s:svg/s:path', $namespace)
$culture = [System.Globalization.CultureInfo]::InvariantCulture
function Number([string]$value) { [single]::Parse($value, $culture) }
function Render-Icon([int]$size) {
    $scale = 4
    $large = [System.Drawing.Bitmap]::new($size * $scale, $size * $scale)
    $graphics = [System.Drawing.Graphics]::FromImage($large)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.ScaleTransform(($size * $scale / 64.0), ($size * $scale / 64.0))
        $x = Number $rectangle.x; $y = Number $rectangle.y
        $width = Number $rectangle.width; $height = Number $rectangle.height
        $radius = Number $rectangle.rx; $diameter = $radius * 2
        $rounded = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($rectangle.fill))
        try {
            $rounded.AddArc($x, $y, $diameter, $diameter, 180, 90)
            $rounded.AddArc(($x + $width - $diameter), $y, $diameter, $diameter, 270, 90)
            $rounded.AddArc(($x + $width - $diameter), ($y + $height - $diameter), $diameter, $diameter, 0, 90)
            $rounded.AddArc($x, ($y + $height - $diameter), $diameter, $diameter, 90, 90)
            $rounded.CloseFigure()
            $graphics.FillPath($background, $rounded)
        } finally { $rounded.Dispose(); $background.Dispose() }
        foreach ($path in $paths) {
            # This mark uses only move/line/close commands; reject unsupported source geometry.
            if ($path.d -match '[A-KN-Yac-kn-y]') { throw 'Unsupported SVG path command' }
            $coordinates = [regex]::Matches($path.d, '-?\d+(?:\.\d+)?')
            $points = [System.Drawing.PointF[]]::new($coordinates.Count / 2)
            for ($index = 0; $index -lt $coordinates.Count; $index += 2) {
                $points[$index / 2] = [System.Drawing.PointF]::new((Number $coordinates[$index].Value), (Number $coordinates[$index + 1].Value))
            }
            if ($path.fill -and $path.fill -ne 'none') {
                $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($path.fill))
                try { $graphics.FillPolygon($brush, $points) } finally { $brush.Dispose() }
            } else {
                $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($path.stroke), (Number $path.'stroke-width'))
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                try { $graphics.DrawLines($pen, $points) } finally { $pen.Dispose() }
            }
        }
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $scaledGraphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $scaledGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $scaledGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $scaledGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scaledGraphics.DrawImage($large, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            $stream = [System.IO.MemoryStream]::new()
            try { $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); return ,$stream.ToArray() }
            finally { $stream.Dispose() }
        } finally { $scaledGraphics.Dispose(); $bitmap.Dispose() }
    } finally { $graphics.Dispose(); $large.Dispose() }
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Render-Icon $_) })
$iconStream = [System.IO.File]::Create((Join-Path $assetDirectory 'glint.ico'))
$writer = [System.IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
[System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'glint.png'), (Render-Icon 512))
[System.IO.File]::WriteAllBytes((Join-Path $previewDirectory 'glint-preview.png'), (Render-Icon 160))
[System.IO.File]::WriteAllBytes((Join-Path $previewDirectory 'glint-16.png'), $frames[0])
Write-Output "Generated glint.ico ($($sizes -join ', ') px) and glint.png; previews: artifacts/icon-previews"
