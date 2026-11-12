Add-Type -AssemblyName System.Drawing

$bitmap = [System.Drawing.Bitmap]::new(64, 64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$background = [System.Drawing.Color]::FromArgb(18, 24, 32)
$amber = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(243, 176, 88))
$cyan = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(119, 210, 199))
$line = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(119, 210, 199), 2)

$graphics.Clear($background)
$graphics.DrawRectangle($line, 3, 3, 57, 57)
$graphics.FillRectangle($amber, 11, 11, 8, 43)
$graphics.FillRectangle($amber, 18, 11, 24, 8)
$graphics.FillRectangle($amber, 39, 18, 8, 14)
$graphics.FillRectangle($amber, 18, 29, 24, 8)
$graphics.FillRectangle($amber, 29, 36, 8, 8)
$graphics.FillRectangle($amber, 36, 42, 8, 7)
$graphics.FillRectangle($amber, 43, 48, 8, 7)
$graphics.FillRectangle($cyan, 52, 10, 4, 4)
$graphics.FillRectangle($cyan, 52, 18, 4, 4)

$size = 64
$xorBytes = $size * $size * 4
$maskBytes = ($size / 8) * $size
$stream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'remember.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]$size)
$writer.Write([byte]$size)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32](40 + $xorBytes + $maskBytes))
$writer.Write([uint32]22)

$writer.Write([uint32]40)
$writer.Write([int32]$size)
$writer.Write([int32]($size * 2))
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]0)
$writer.Write([uint32]$xorBytes)
$writer.Write([int32]0)
$writer.Write([int32]0)
$writer.Write([uint32]0)
$writer.Write([uint32]0)

for ($y = $size - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $size; $x++) {
        $pixel = $bitmap.GetPixel($x, $y)
        $writer.Write([byte]$pixel.B)
        $writer.Write([byte]$pixel.G)
        $writer.Write([byte]$pixel.R)
        $writer.Write([byte]$pixel.A)
    }
}
$writer.Write([byte[]]::new($maskBytes))
$writer.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$amber.Dispose()
$cyan.Dispose()
$line.Dispose()
