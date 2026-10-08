param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\Assets'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Add-Type -AssemblyName System.Drawing

$resolvedSource = (Resolve-Path -LiteralPath $SourcePath).Path
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$source = [System.Drawing.Bitmap]::FromFile($resolvedSource)
$iconImages = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 24, 32, 48, 64, 128, 256)

try {
    # Trim the supplied white canvas while retaining the complete circular artwork.
    $cropSize = [Math]::Min(1200, [Math]::Min($source.Width, $source.Height))
    $cropX = [Math]::Max(0, [Math]::Floor(($source.Width - $cropSize) / 2))
    $sourceRectangle = [System.Drawing.Rectangle]::new($cropX, 0, $cropSize, $cropSize)

    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $bitmap.SetResolution(96, 96)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

                $clip = [System.Drawing.Drawing2D.GraphicsPath]::new()
                try {
                    $clip.AddEllipse(0.5, 0.5, $size - 1, $size - 1)
                    $graphics.SetClip($clip)
                    $destination = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
                    $graphics.DrawImage($source, $destination, $sourceRectangle, [System.Drawing.GraphicsUnit]::Pixel)
                }
                finally {
                    $clip.Dispose()
                }
            }
            finally {
                $graphics.Dispose()
            }

            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $iconImages.Add($stream.ToArray())
            }
            finally {
                $stream.Dispose()
            }

            if ($size -eq 256) {
                $bitmap.Save((Join-Path $resolvedOutput 'AppLogo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $source.Dispose()
}

$iconPath = Join-Path $resolvedOutput 'AppIcon.ico'
$output = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$iconImages[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $iconImages[$index].Length
    }

    foreach ($image in $iconImages) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $output.Dispose()
}

Write-Output "Generated AppLogo.png and AppIcon.ico in $resolvedOutput"
