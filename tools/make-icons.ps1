# Gera os ícones 16x16 dos plugins (src\<Plugin>\icon.png).
# Desenha em 256x256 com antialias e reduz com alta qualidade para ficar nítido.
# Uso: .\tools\make-icons.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

function New-Canvas {
    $bmp = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    return @{ Bmp = $bmp; G = $g }
}

function Save-Small($bmp, $path) {
    $small = New-Object System.Drawing.Bitmap 16, 16, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($bmp, 0, 0, 16, 16)
    $g.Dispose()
    $small.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose(); $bmp.Dispose()
}

function Round-Rect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Color($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

# --- Erosão de Contorno: forma clara com a forma "comida" (menor, escura) por dentro ---
$c = New-Canvas
$outer = Round-Rect 16 16 224 224 64
$c.G.FillPath((New-Object System.Drawing.SolidBrush (Color '#9CCBFF')), $outer)
$c.G.DrawPath((New-Object System.Drawing.Pen (Color '#1F5FA8'), 18), $outer)
$inner = Round-Rect 76 76 104 104 28
$c.G.FillPath((New-Object System.Drawing.SolidBrush (Color '#1F5FA8')), $inner)
Save-Small $c.Bmp "$root\src\ContourErosion\icon.png"

# --- Inclinação: retângulo cisalhado (paralelogramo) ---
$c = New-Canvas
$pts = @(
    (New-Object System.Drawing.PointF 100, 28),
    (New-Object System.Drawing.PointF 244, 28),
    (New-Object System.Drawing.PointF 156, 228),
    (New-Object System.Drawing.PointF 12, 228)
)
$c.G.FillPolygon((New-Object System.Drawing.SolidBrush (Color '#FFB347')), $pts)
$pen = New-Object System.Drawing.Pen (Color '#B45F06'), 18
$pen.LineJoin = 'Round'
$c.G.DrawPolygon($pen, $pts)
Save-Small $c.Bmp "$root\src\Skew\icon.png"

Write-Host 'Ícones gerados.'
