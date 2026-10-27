param([string]$OutputPath = "$PSScriptRoot\..\src\YuWangCloudSync.ShellExt\Assets\synced.ico")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$frames = @(16, 20, 24, 32, 48, 64) | ForEach-Object {
    $size = $_
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 25, 153, 66))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, ($size * 0.12))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.FillEllipse($green, ($size * 0.05), ($size * 0.05), ($size * 0.9), ($size * 0.9))
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($size * 0.27, $size * 0.51),
        [System.Drawing.PointF]::new($size * 0.44, $size * 0.67),
        [System.Drawing.PointF]::new($size * 0.74, $size * 0.34))
    $graphics.DrawLines($pen, $points)
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $pen.Dispose()
    $green.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))) | Out-Null
$file = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([byte]$frame.Size)
        $writer.Write([byte]$frame.Size)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
