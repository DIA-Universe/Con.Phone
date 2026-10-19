$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path -Parent $PSScriptRoot
$sizes = @(16,24,32,48,64,128,256)
$images = @()
foreach ($size in ($sizes + 512)) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size/100.0,$size/100.0)
    $bg = [Drawing.Drawing2D.GraphicsPath]::new()
    $bg.AddArc(0,0,48,48,180,90); $bg.AddArc(52,0,48,48,270,90)
    $bg.AddArc(52,52,48,48,0,90); $bg.AddArc(0,52,48,48,90,90); $bg.CloseFigure()
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#103B46'))
    $g.FillPath($brush,$bg)
    foreach ($second in @($false,$true)) {
        $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($(if ($second) {'#65E0CF'} else {'#FFFFFF'})),12)
        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $path = [Drawing.Drawing2D.GraphicsPath]::new()
        if ($second) {
            $path.AddLine(70,76,70,44); $path.AddBezier(70,44,70,37.333,66.667,34,60,34); $path.AddLine(60,34,42,34)
        } else {
            $path.AddLine(30,24,30,56); $path.AddBezier(30,56,30,62.667,33.333,66,40,66); $path.AddLine(40,66,58,66)
        }
        $g.DrawPath($pen,$path); $path.Dispose(); $pen.Dispose()
    }
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 512) { [IO.File]::WriteAllBytes((Join-Path $repo 'assets/brand/linko.png'),$stream.ToArray()) }
    else { $images += ,$stream.ToArray() }
    $stream.Dispose(); $brush.Dispose(); $bg.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$ico = [IO.MemoryStream]::new(); $writer = [IO.BinaryWriter]::new($ico)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset); $offset += $images[$i].Length
}
foreach ($bytes in $images) {$writer.Write([byte[]]$bytes)}
[IO.File]::WriteAllBytes((Join-Path $repo 'src/ConPhone.Windows/Assets/linko.ico'),$ico.ToArray())
$writer.Dispose(); $ico.Dispose()
