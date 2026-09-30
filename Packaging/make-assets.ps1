param(
    [string]$SvgDir = (Join-Path $PSScriptRoot "..\assets"),
    [string]$OutDir = (Join-Path $PSScriptRoot "Assets"),
    [string]$StoreDir = (Join-Path $PSScriptRoot "store"),
    [string]$AppAssetsDir = (Join-Path $PSScriptRoot "..\WifiSpeedWidget\Assets")
)

Add-Type -AssemblyName System.Drawing

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge is needed to render the logo. Install it or render assets\logo.svg yourself." }

$work = Join-Path $env:TEMP "speedline-assets"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, $OutDir, $StoreDir | Out-Null

foreach ($name in "logo", "logo-small", "tray-dark", "tray-light") {
    Copy-Item (Join-Path $SvgDir "$name.svg") $work
    $htmlPath = Join-Path $work "$name.html"
    Set-Content $htmlPath "<!doctype html><html><head><style>html,body{margin:0;background:transparent}img{display:block;width:256px;height:256px}</style></head><body><img src='$name.svg'></body></html>" -Encoding utf8
    $url = "file:///" + $htmlPath.Replace([char]92, [char]47)
    & $edge --headless=new --disable-gpu --hide-scrollbars --no-first-run "--user-data-dir=$work\profile" --default-background-color=00000000 --force-device-scale-factor=4 --window-size=256,256 "--screenshot=$work\$name-1024.png" $url 2>&1 | Out-Null
    if (-not (Test-Path "$work\$name-1024.png")) { throw "Rendering $name failed" }
}

function Save-Resized([string]$source, [int]$size, [string]$destination) {
    $image = [System.Drawing.Image]::FromFile($source)
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.DrawImage($image, 0, 0, $size, $size)
    $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose(); $image.Dispose()
}

function Save-Ico([string]$source, [int[]]$sizes, [string]$destination) {
    $frames = foreach ($size in $sizes) {
        $png = Join-Path $work "ico-$size.png"
        Save-Resized $source $size $png
        , [System.IO.File]::ReadAllBytes($png)
    }
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = [byte]($sizes[$i] % 256)
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($destination, $stream.ToArray())
    $writer.Dispose()
}

function Pick-Source([int]$size) {
    if ($size -le 24) { "$work\logo-small-1024.png" } else { "$work\logo-1024.png" }
}

Get-ChildItem $OutDir -Filter *.png | Remove-Item -Force

foreach ($entry in @(@("Square44x44Logo", 44), @("Square150x150Logo", 150), @("StoreLogo", 50))) {
    foreach ($scale in 100, 200, 400) {
        $size = [int]($entry[1] * $scale / 100)
        Save-Resized (Pick-Source $size) $size (Join-Path $OutDir "$($entry[0]).scale-$scale.png")
    }
}

foreach ($size in 16, 24, 32, 48, 256) {
    Save-Resized (Pick-Source $size) $size (Join-Path $OutDir "Square44x44Logo.targetsize-$size.png")
    Save-Resized (Pick-Source $size) $size (Join-Path $OutDir "Square44x44Logo.targetsize-${size}_altform-unplated.png")
}

Save-Resized "$work\logo-1024.png" 300 (Join-Path $StoreDir "icon-300x300.png")
Save-Resized "$work\logo-1024.png" 1080 (Join-Path $StoreDir "icon-1080x1080.png")

New-Item -ItemType Directory -Force $AppAssetsDir | Out-Null
foreach ($name in "tray-dark", "tray-light") {
    Save-Ico "$work\$name-1024.png" @(16, 20, 24, 32, 40, 48, 64) (Join-Path $AppAssetsDir "$name.ico")
}

"Generated $((Get-ChildItem $OutDir -Filter *.png).Count) package assets, 2 Store listing icons and 2 tray icons."
