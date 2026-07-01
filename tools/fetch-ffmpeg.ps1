# Downloads a static ffmpeg.exe and places it next to the app so the "Panel Video"
# feature can transcode. Run once:  powershell -ExecutionPolicy Bypass -File tools\fetch-ffmpeg.ps1
# The binary is git-ignored (it is large); this script is how you "bundle" it.

$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot "..\src\RyuoBrightnessFix\ffmpeg.exe"
$dest = [System.IO.Path]::GetFullPath($dest)
if (Test-Path $dest) { Write-Host "ffmpeg already present: $dest"; return }

$zipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$tmp = Join-Path $env:TEMP ("ffmpeg_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp "ffmpeg.zip"
    Write-Host "Downloading ffmpeg (~90 MB) from $zipUrl ..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $zip
    Write-Host "Extracting..."
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $exe = Get-ChildItem -Path $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    if (-not $exe) { throw "ffmpeg.exe not found in the downloaded archive." }
    Copy-Item $exe.FullName $dest -Force
    Write-Host "ffmpeg.exe placed at: $dest" -ForegroundColor Green
    Write-Host "Rebuild the app; it will copy ffmpeg.exe next to RyuoBrightnessFix.exe."
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
