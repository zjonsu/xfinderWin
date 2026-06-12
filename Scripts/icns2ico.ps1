# AppIcon.icns -> AppIcon.ico 변환 — 가장 큰 PNG를 추출해 16/32/48/64/128/256으로 리샘플
param(
    [string]$In = "D:\project\xFinder-mac-src\Resources\AppIcon.icns",
    [string]$Out = "D:\project\xFinder\Resources\AppIcon.ico"
)
Add-Type -AssemblyName System.Drawing

$bytes = [IO.File]::ReadAllBytes($In)
if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'icns') { throw 'icns 형식이 아님' }

$pngSig = [byte[]](0x89, 0x50, 0x4E, 0x47)
$best = $null; $bestW = 0
$pos = 8
while ($pos + 8 -le $bytes.Length) {
    $len = ($bytes[$pos+4] -shl 24) -bor ($bytes[$pos+5] -shl 16) -bor ($bytes[$pos+6] -shl 8) -bor $bytes[$pos+7]
    if ($len -lt 8 -or $pos + $len -gt $bytes.Length) { break }
    $dataLen = $len - 8
    if ($dataLen -gt 24) {
        $off = $pos + 8
        $isPng = $true
        for ($i = 0; $i -lt 4; $i++) { if ($bytes[$off+$i] -ne $pngSig[$i]) { $isPng = $false; break } }
        if ($isPng) {
            $w = ($bytes[$off+16] -shl 24) -bor ($bytes[$off+17] -shl 16) -bor ($bytes[$off+18] -shl 8) -bor $bytes[$off+19]
            if ($w -gt $bestW) {
                $bestW = $w
                $best = New-Object byte[] $dataLen
                [Array]::Copy($bytes, $off, $best, 0, $dataLen)
            }
        }
    }
    $pos += $len
}
if ($null -eq $best) { throw 'PNG 청크 없음' }

$srcStream = New-Object IO.MemoryStream(,$best)
$src = [Drawing.Image]::FromStream($srcStream)

$sizes = 16, 32, 48, 64, 128, 256
$pngBlobs = @()
foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap($s, $s)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $m = New-Object IO.MemoryStream
    $bmp.Save($m, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngBlobs += [pscustomobject]@{ Width = $s; Data = $m.ToArray() }
}
$src.Dispose()

$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngBlobs.Count)
$offset = 6 + 16 * $pngBlobs.Count
foreach ($img in $pngBlobs) {
    $dim = if ($img.Width -ge 256) { 0 } else { $img.Width }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$img.Data.Length); $bw.Write([uint32]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $pngBlobs) { $bw.Write($img.Data) }
$bw.Flush()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[IO.File]::WriteAllBytes($Out, $ms.ToArray())
"OK: $Out (source ${bestW}px -> $(($pngBlobs | ForEach-Object Width) -join ','))"
