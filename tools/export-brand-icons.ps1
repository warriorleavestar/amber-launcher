<#
从已批准的透明 PNG 导出 Jay 的应用图标，只等比缩放，不裁切、描边或重绘。
默认输出 512 像素 PNG 和包含 16–256 像素图层的 ICO，需要 Windows 的 System.Drawing。
用法：powershell -NoProfile -File tools/export-brand-icons.ps1 [-Source <原图>] [-OutputDirectory <目录>]
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$OutputDirectory,
    [ValidateRange(16, 1024)][int]$PngSize = 512
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not $Source) { $Source = Join-Path $PSScriptRoot '..\docs\brand\jay-feather-amber-v1.png' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\src\Amber.App\Assets' }
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
$sourceImage = [System.Drawing.Image]::FromFile($sourcePath)

function Get-ScaledPng([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $scale = [Math]::Min($Size / $sourceImage.Width, $Size / $sourceImage.Height)
        $width = [Math]::Max(1, [int][Math]::Round($sourceImage.Width * $scale))
        $height = [Math]::Max(1, [int][Math]::Round($sourceImage.Height * $scale))
        $destination = [System.Drawing.Rectangle]::new([int](($Size - $width) / 2), [int](($Size - $height) / 2), $width, $height)
        $graphics.DrawImage($sourceImage, $destination, 0, 0, $sourceImage.Width, $sourceImage.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $attributes.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    [System.IO.File]::WriteAllBytes((Join-Path $outputPath 'jay.png'), (Get-ScaledPng $PngSize))
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) { $frames.Add((Get-ScaledPng $size)) }

    # ICO 的目录使用小端序，每个图层直接保存透明 PNG；256 像素在目录里用 0 表示。
    $ico = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($ico)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes((Join-Path $outputPath 'jay.ico'), $ico.ToArray())
    }
    finally {
        $writer.Dispose()
        $ico.Dispose()
    }
    Write-Host "已导出 $outputPath\jay.png（$PngSize 像素）和 jay.ico（$($sizes -join '/') 像素）。"
}
finally { $sourceImage.Dispose() }
