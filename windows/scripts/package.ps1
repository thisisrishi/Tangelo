# Publishes a self-contained Tangelo for Windows x64 with bundled ffmpeg and the BG-removal model,
# then builds the portable zip and (if Inno Setup is installed) the installer.
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'artifacts'
$app = Join-Path $out 'Tangelo'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $app | Out-Null

& (Join-Path $PSScriptRoot 'fetch-model.ps1')

dotnet publish (Join-Path $root 'src/Tangelo.App') -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -p:Version=$Version -o $app
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# ffmpeg (GPL "shared" build from BtbN): newest stable line. ffmpeg.exe and ffprobe.exe share one set of DLLs.
$ffZip = Join-Path $out 'ffmpeg.zip'
$headers = @{ 'User-Agent' = 'tangelo-build' }
if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }
$assets = (Invoke-RestMethod 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest' -Headers $headers).assets
$ff = $assets | Where-Object { $_.name -match '^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-shared-\d+\.\d+\.zip$' } |
  Sort-Object { [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$1') * 100 + [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$2') } -Descending |
  Select-Object -First 1
if (-not $ff) { throw 'No stable ffmpeg build found' }
Write-Host "Bundling $($ff.name)"
Invoke-WebRequest $ff.browser_download_url -OutFile $ffZip
Expand-Archive $ffZip -DestinationPath (Join-Path $out 'ffmpeg')
$ffDir = Get-ChildItem (Join-Path $out 'ffmpeg') -Directory | Select-Object -First 1
New-Item -ItemType Directory (Join-Path $app 'tools\licenses') -Force | Out-Null
Get-ChildItem (Join-Path $ffDir.FullName 'bin') -Include 'ffmpeg.exe', 'ffprobe.exe', '*.dll' -Recurse | Copy-Item -Destination (Join-Path $app 'tools')
Copy-Item (Join-Path $ffDir.FullName 'LICENSE.txt') (Join-Path $app 'tools\licenses\ffmpeg-LICENSE.txt')
@"
ffmpeg and ffprobe are separate programs distributed under the GNU GPL v3.
Build: $($ffDir.Name) from https://github.com/BtbN/FFmpeg-Builds
Source code: https://ffmpeg.org/download.html (and https://github.com/BtbN/FFmpeg-Builds for build scripts).
"@ | Set-Content (Join-Path $app 'tools\licenses\ffmpeg-NOTICE.txt')
Copy-Item (Join-Path $root '..\LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $app 'THIRD-PARTY-NOTICES.md')

# Portable zip
$zip = Join-Path $out "Tangelo-windows-x64-portable.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip

# Installer
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
  & $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/O$out" (Join-Path $root 'installer\Tangelo.iss')
  if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }
} else { Write-Warning 'Inno Setup not found; skipping installer.' }
Get-ChildItem $out -File | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
