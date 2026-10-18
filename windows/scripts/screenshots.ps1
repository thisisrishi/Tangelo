# Renders each main window of the packaged app to PNG using --demo/--snap.
param([string]$Exe, [string]$Samples, [string]$Out)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory $Out -Force | Out-Null
$png = Join-Path $Samples 'sample.png'; $pdf = Join-Path $Samples 'notes merged.pdf'
if (-not (Test-Path $pdf)) { $pdf = (Get-ChildItem $Samples -Filter '*merged*.pdf' | Select-Object -First 1).FullName }
$video = Join-Path $Samples 'clip.mp4'; $wav = Join-Path $Samples 'tone.wav'
$shots = @(
  @('wheel-conversions', $png), @('wheel-tools', $png), @('wheel-actions', $png),
  @('float', $png, $pdf, $video, $wav), @('tray', $png), @('settings'), @('welcome'),
  @('Compress', $png, $video, $pdf), @('AnnotateImage', $png), @('FrameImage', $png), @('EditImage', $png),
  @('RedactImage', $png), @('CropImage', $png), @('TrimAudio', $wav), @('TrimVideo', $video),
  @('SplitVideo', $video), @('OrganizePdf', $pdf), @('AudioToVideo', $wav), @('BatchRename', $png, $pdf, $wav)
)
foreach ($s in $shots) {
  $name = $s[0]; $files = $s[1..($s.Length)] | Where-Object { $_ }
  $target = Join-Path $Out "$name.png"
  $args = @('--demo', $name) + ($files | ForEach-Object { "`"$_`"" }) + @('--snap', "`"$target`"")
  $p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru
  if (-not $p.WaitForExit(40000)) { $p.Kill(); Write-Warning "$name timed out" }
  if (Test-Path $target) { Write-Host "captured $name" } else { Write-Warning "no screenshot for $name (exit $($p.ExitCode))" }
}
