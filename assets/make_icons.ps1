# Kirihari Lite / Pro icon generator (System.Drawing). Output: app.ico in each project folder.
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size, [string]$kind) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    if ($kind -eq 'pro') { $c1 = '#3A5BD9'; $c2 = '#1E2F78' } else { $c1 = '#3FB594'; $c2 = '#1F6B59' }
    $rect = New-Object System.Drawing.RectangleF (8 * $s), (8 * $s), (240 * $s), (240 * $s)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 48 * $s
    $path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
    $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
    $path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.ColorTranslator]::FromHtml($c1)), ([System.Drawing.ColorTranslator]::FromHtml($c2)), 90
    $g.FillPath($brush, $path)

    # two film clips with a gap between them
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    $y = 92 * $s; $h = 72 * $s
    $g.FillRectangle($white, 36 * $s, $y, 76 * $s, $h)
    $g.FillRectangle($white, 144 * $s, $y, 76 * $s, $h)

    # cut line
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#FFD040')), (14 * $s)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 140 * $s, 56 * $s, 116 * $s, 200 * $s)

    if ($kind -eq 'pro') {
        $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 208, 64))
        $g.FillRectangle($bar, 36 * $s, 196 * $s, 184 * $s, 14 * $s)
    }
    $g.Dispose()
    return $bmp
}

function Save-Ico([string]$path, [string]$kind) {
    $sizes = 256, 64, 48, 32, 16
    $pngs = @()
    foreach ($sz in $sizes) {
        $bmp = New-Frame $sz $kind
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]
        $b = if ($sz -ge 256) { 0 } else { $sz }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close(); $fs.Close()
}

$root = Split-Path -Parent $PSScriptRoot
Save-Ico (Join-Path $root 'src\Kirihari.Lite\app.ico') 'lite'
Save-Ico (Join-Path $root 'src\Kirihari.Pro\app.ico') 'pro'
Write-Output 'done'
