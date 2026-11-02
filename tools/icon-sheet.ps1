Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$src = "D:\Projects\Program\FocusKeep\preview"
$states = @(
    @{ name = "On";         file = "icon-On.png" },
    @{ name = "On+Locked";  file = "icon-On-Locked.png" },
    @{ name = "Off";        file = "icon-Off.png" },
    @{ name = "Off+Locked"; file = "icon-Off-Locked.png" }
)

# 大图用于看细节 + 托盘真实尺寸
$sizes = @(128, 48, 32, 24, 16)
$cellW = 150
$rowH = 150
$sheet = New-Object System.Drawing.Bitmap(($cellW * $sizes.Count), ($rowH * $states.Count))
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(32, 33, 36))

for ($r = 0; $r -lt $states.Count; $r++) {
    $png = [System.Drawing.Image]::FromFile((Join-Path $src $states[$r].file))
    for ($c = 0; $c -lt $sizes.Count; $c++) {
        $s = $sizes[$c]
        $x = $c * $cellW + [int](($cellW - $s) / 2)
        $y = $r * $rowH + [int](($rowH - $s) / 2)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($png, $x, $y, $s, $s)
    }
    $png.Dispose()
}
$g.Dispose()
$sheet.Save("D:\Projects\Program\FocusKeep\preview\icon-sheet.png", [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()

# 单独放大一张锁定状态，方便看锁
$big = New-Object System.Drawing.Bitmap(512, 512)
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.Clear([System.Drawing.Color]::FromArgb(32, 33, 36))
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$p = [System.Drawing.Image]::FromFile((Join-Path $src "icon-On-Locked.png"))
$g2.DrawImage($p, 0, 0, 512, 512)
$p.Dispose()
$g2.Dispose()
$big.Save("D:\Projects\Program\FocusKeep\preview\icon-lock-zoom.png", [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
"ok"
