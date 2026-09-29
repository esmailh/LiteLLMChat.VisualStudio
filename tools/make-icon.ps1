# Draws the extension's icon: the same mark the chat's header and its empty state use, on the accent the shell pairs it
# with. It is drawn as geometry rather than rendered as the ✦ character, because a glyph depends on a font being
# installed and on that font carrying U+2726 — and whether it does is not something this script, or the machine that
# builds the package, can be sure of. Four cubic curves whose control points sit near the centre give the concave
# four-pointed sparkle: the tips are sharp and the sides bow inward toward the middle.
#
# Run from the project root (F:\New folder (3)) after changing a number here; the PNG it writes is what the package
# ships, so it has to be regenerated rather than edited.
[CmdletBinding()]
param([string]$Path = 'src\LiteLLM.VisualStudio\Resources\Icon.png')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$size = 128
$corner = 14
$half = $size / 2.0
$reach = 45.0        # how far a tip sits from the centre
$waist = 0.26        # how far out from the centre the control points sit; smaller is a thinner, spikier star, and at
                     # the 16-32px the extension list draws this at, the thinnest version loses its tips entirely
$accent = [System.Drawing.Color]::FromArgb(255, 0, 122, 204)   # the accent Visual Studio's Blue theme is known by
$mark = [System.Drawing.Color]::White

# Built through -ArgumentList rather than Type(a, b): inside the Type(...) form an argument containing an operator is
# read as part of a comma list, and the constructor then receives an array where it wanted a number.
$top = New-Object System.Drawing.PointF -ArgumentList @([float]$half, [float]($half - $reach))
$right = New-Object System.Drawing.PointF -ArgumentList @([float]($half + $reach), [float]$half)
$bottom = New-Object System.Drawing.PointF -ArgumentList @([float]$half, [float]($half + $reach))
$left = New-Object System.Drawing.PointF -ArgumentList @([float]($half - $reach), [float]$half)
$tips = @($top, $right, $bottom, $left, $top)

$bitmap = New-Object System.Drawing.Bitmap -ArgumentList @($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$canvas = [System.Drawing.Graphics]::FromImage($bitmap)
$canvas.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$canvas.Clear([System.Drawing.Color]::Transparent)

$diameter = $corner * 2
$background = New-Object System.Drawing.Drawing2D.GraphicsPath
$background.AddArc(0, 0, $diameter, $diameter, 180, 90)
$background.AddArc(($size - $diameter), 0, $diameter, $diameter, 270, 90)
$background.AddArc(($size - $diameter), ($size - $diameter), $diameter, $diameter, 0, 90)
$background.AddArc(0, ($size - $diameter), $diameter, $diameter, 90, 90)
$background.CloseFigure()
$canvas.FillPath((New-Object System.Drawing.SolidBrush -ArgumentList $accent), $background)

$star = New-Object System.Drawing.Drawing2D.GraphicsPath
$star.StartFigure()
for ($i = 0; $i -lt 4; $i++) {
    $from = $tips[$i]
    $to = $tips[$i + 1]
    # Each control point is pulled out of the centre toward the tip it belongs to, which is what keeps the curve concave
    # instead of letting it bulge out into a plain diamond.
    $c1 = New-Object System.Drawing.PointF -ArgumentList @([float]($half + ($from.X - $half) * $waist), [float]($half + ($from.Y - $half) * $waist))
    $c2 = New-Object System.Drawing.PointF -ArgumentList @([float]($half + ($to.X - $half) * $waist), [float]($half + ($to.Y - $half) * $waist))
    $star.AddBezier($from, $c1, $c2, $to)
}
$star.CloseFigure()
$canvas.FillPath((New-Object System.Drawing.SolidBrush -ArgumentList $mark), $star)

$canvas.Dispose()
$target = [System.IO.Path]::GetFullPath($Path)
$directory = Split-Path -Parent $target
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
$bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$file = Get-Item $target
Write-Host ("wrote " + $file.FullName + "  " + $file.Length + " bytes  " + $size + "x" + $size)
