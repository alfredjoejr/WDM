param(
    [string]$SourceImage = "C:\Users\joash\.gemini\antigravity-ide\brain\f6c89a57-417d-4bff-9fbe-f46eebaafa88\wdm_logo_app_1792911550560.jpg"
)

Add-Type -AssemblyName System.Drawing

$srcBmp = [System.Drawing.Bitmap]::FromFile($SourceImage)

function Resize-Bitmap([System.Drawing.Bitmap]$source, [int]$width, [int]$height) {
    $dest = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dest)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($source, 0, 0, $width, $height)
    $g.Dispose()
    return $dest
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$path) {
    $dir = [System.IO.Path]::GetDirectoryName($path)
    if (-not [System.IO.Directory]::Exists($dir)) {
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Build-Ico([System.Drawing.Bitmap]$sourceBmp, [int[]]$sizes, [string]$outPath) {
    $dir = [System.IO.Path]::GetDirectoryName($outPath)
    if (-not [System.IO.Directory]::Exists($dir)) {
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    }

    $pngDataList = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($sz in $sizes) {
        $resized = Resize-Bitmap $sourceBmp $sz $sz
        $ms = [System.IO.MemoryStream]::new()
        $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngDataList.Add($ms.ToArray())
        $ms.Dispose()
        $resized.Dispose()
    }

    $fs = [System.IO.FileStream]::new($outPath, [System.IO.FileMode]::Create)
    $bw = [System.IO.BinaryWriter]::new($fs)

    # ICONDIR
    $bw.Write([uint16]0) # Reserved
    $bw.Write([uint16]1) # Type (1 = ICO)
    $bw.Write([uint16]$sizes.Length) # Count

    $offset = 6 + ($sizes.Length * 16)

    for ($i = 0; $i -lt $sizes.Length; $i++) {
        $sz = $sizes[$i]
        $w = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
        $h = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
        $len = $pngDataList[$i].Length

        # ICONDIRENTRY
        $bw.Write($w) # Width
        $bw.Write($h) # Height
        $bw.Write([byte]0) # ColorCount
        $bw.Write([byte]0) # Reserved
        $bw.Write([uint16]1) # Planes
        $bw.Write([uint16]32) # BitCount
        $bw.Write([uint32]$len) # BytesInRes
        $bw.Write([uint32]$offset) # ImageOffset

        $offset += $len
    }

    for ($i = 0; $i -lt $sizes.Length; $i++) {
        $bw.Write($pngDataList[$i])
    }

    $bw.Flush()
    $bw.Close()
    $fs.Close()
}

$root = "$PSScriptRoot\.."

Write-Host "Generating PNG assets..."
$bmp512 = Resize-Bitmap $srcBmp 512 512
Save-Png $bmp512 "$root\assets\logo.png"
Save-Png $bmp512 "$root\src\WDM.UI\wwwroot\assets\logo.png"

$bmp256 = Resize-Bitmap $srcBmp 256 256
Save-Png $bmp256 "$root\assets\icon-256.png"

$bmp128 = Resize-Bitmap $srcBmp 128 128
Save-Png $bmp128 "$root\extension\icons\icon-128.png"

$bmp48 = Resize-Bitmap $srcBmp 48 48
Save-Png $bmp48 "$root\extension\icons\icon-48.png"

$bmp32 = Resize-Bitmap $srcBmp 32 32
Save-Png $bmp32 "$root\src\WDM.UI\wwwroot\assets\icon-32.png"

$bmp16 = Resize-Bitmap $srcBmp 16 16
Save-Png $bmp16 "$root\extension\icons\icon-16.png"

Write-Host "Generating multi-resolution Windows ICO..."
$icoSizes = @(16, 32, 48, 64, 128, 256)
Build-Ico $srcBmp $icoSizes "$root\assets\icon.ico"
Build-Ico $srcBmp $icoSizes "$root\src\WDM.UI\assets\icon.ico"
Build-Ico $srcBmp $icoSizes "$root\src\WDM.UI\wwwroot\favicon.ico"

$srcBmp.Dispose()
Write-Host "Icon generation completed successfully!"
