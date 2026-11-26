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

# --- Rotação: quadrado girado com seta curva ---
$c = New-Canvas
$g = $c.G
$state = $g.Save()
$g.TranslateTransform(120, 136)
$g.RotateTransform(-28)
$g.TranslateTransform(-56, -56)
$sq = Round-Rect 0 0 112 112 20
$g.FillPath((New-Object System.Drawing.SolidBrush (Color '#8FD19E')), $sq)
$g.DrawPath((New-Object System.Drawing.Pen (Color '#2E7D32'), 16), $sq)
$g.Restore($state)

# Arco no canto superior direito, sentido horário, com ponta de seta no fim.
$cx = 128.0; $cy = 128.0; $r = 104.0
$startDeg = -75.0; $sweep = 65.0
$arcPen = New-Object System.Drawing.Pen (Color '#1B4D20'), 18
$arcPen.StartCap = 'Round'
$g.DrawArc($arcPen, ($cx - $r), ($cy - $r), (2 * $r), (2 * $r), $startDeg, $sweep)
$endRad = ($startDeg + $sweep) * [Math]::PI / 180
$ex = $cx + $r * [Math]::Cos($endRad); $ey = $cy + $r * [Math]::Sin($endRad)
$tx = -[Math]::Sin($endRad); $ty = [Math]::Cos($endRad)      # tangente (sentido horário)
$nx = [Math]::Cos($endRad);  $ny = [Math]::Sin($endRad)      # normal (para fora)
$tip = @(
    (New-Object System.Drawing.PointF ($ex + $tx * 46), ($ey + $ty * 46)),
    (New-Object System.Drawing.PointF ($ex + $nx * 34), ($ey + $ny * 34)),
    (New-Object System.Drawing.PointF ($ex - $nx * 34), ($ey - $ny * 34))
)
$g.FillPolygon((New-Object System.Drawing.SolidBrush (Color '#1B4D20')), $tip)
Save-Small $c.Bmp "$root\src\Rotate\icon.png"
# --- ShapeFit: um círculo (objeto) sendo esticado até preencher a forma de destino (quadrado) ---
$c = New-Canvas
$g = $c.G
$box = Round-Rect 20 20 216 216 36
$g.FillPath((New-Object System.Drawing.SolidBrush (Color '#D1C4E9')), $box)
$g.DrawPath((New-Object System.Drawing.Pen (Color '#4527A0'), 16), $box)
$g.FillEllipse((New-Object System.Drawing.SolidBrush (Color '#7E57C2')), 56, 56, 144, 144)
# quatro setinhas saindo do círculo em direção aos cantos
$arrow = New-Object System.Drawing.SolidBrush (Color '#4527A0')
foreach ($ang in 45, 135, 225, 315) {
    $rad = $ang * [Math]::PI / 180
    $cx = 128 + 90 * [Math]::Cos($rad); $cy = 128 + 90 * [Math]::Sin($rad)
    $tx = [Math]::Cos($rad); $ty = [Math]::Sin($rad)
    $pts = @(
        (New-Object System.Drawing.PointF ($cx + $tx * 26), ($cy + $ty * 26)),
        (New-Object System.Drawing.PointF ($cx - $ty * 20), ($cy + $tx * 20)),
        (New-Object System.Drawing.PointF ($cx + $ty * 20), ($cy - $tx * 20))
    )
    $g.FillPolygon($arrow, $pts)
}
Save-Small $c.Bmp "$root\src\ShapeFit\icon.png"

Write-Host 'Ícones gerados.'

