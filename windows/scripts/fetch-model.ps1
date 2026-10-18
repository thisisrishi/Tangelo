# Downloads the background-removal model and verifies its checksum.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot '..\models\u2netp.onnx'
$expected = '309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8'
if (-not (Test-Path $dest)) {
    Invoke-WebRequest 'https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx' -OutFile $dest
}
$hash = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
if ($hash -ne $expected) { Remove-Item $dest; throw "Model checksum mismatch: $hash" }
Write-Host "Model ready: $dest"
