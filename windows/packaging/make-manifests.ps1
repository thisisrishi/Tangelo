# Generates winget and Scoop manifests for a published release (checksums taken from the release).
# Usage: ./make-manifests.ps1 -Version 1.1.0
param([Parameter(Mandatory)] [string]$Version)
$ErrorActionPreference = 'Stop'
$base = "https://github.com/thisisrishi/Tangelo/releases/download/v$Version"
$out = Join-Path $PSScriptRoot "out\$Version"
New-Item -ItemType Directory $out -Force | Out-Null
$tmp = New-Item -ItemType Directory (Join-Path ([IO.Path]::GetTempPath()) "tangelo-$([guid]::NewGuid())")
$setup = "TangeloSetup-$Version-x64.exe"; $zip = "Tangelo-windows-x64-portable.zip"
Invoke-WebRequest "$base/$setup" -OutFile "$tmp\$setup"
Invoke-WebRequest "$base/$zip" -OutFile "$tmp\$zip"
$setupSha = (Get-FileHash "$tmp\$setup" -Algorithm SHA256).Hash
$zipSha = (Get-FileHash "$tmp\$zip" -Algorithm SHA256).Hash.ToLower()

$winget = Join-Path $out 'winget'
New-Item -ItemType Directory $winget -Force | Out-Null
@"
PackageIdentifier: thisisrishi.Tangelo
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.9.0
"@ | Set-Content "$winget\thisisrishi.Tangelo.yaml"
@"
PackageIdentifier: thisisrishi.Tangelo
PackageVersion: $Version
InstallerType: inno
Scope: user
InstallModes: [interactive, silent, silentWithProgress]
UpgradeBehavior: install
Installers:
  - Architecture: x64
    InstallerUrl: $base/$setup
    InstallerSha256: $setupSha
ManifestType: installer
ManifestVersion: 1.9.0
"@ | Set-Content "$winget\thisisrishi.Tangelo.installer.yaml"
@"
PackageIdentifier: thisisrishi.Tangelo
PackageVersion: $Version
PackageLocale: en-US
Publisher: Rishi Raj Jaiswal
PublisherUrl: https://github.com/thisisrishi
PackageName: Tangelo
PackageUrl: https://github.com/thisisrishi/Tangelo
License: MIT
LicenseUrl: https://github.com/thisisrishi/Tangelo/blob/main/LICENSE
ShortDescription: Convert, edit and move files right where you drag them.
Description: Tangelo opens a conversion, tools or actions wheel under the cursor while you drag files. 45+ formats, 30+ tools (compress, annotate, redact, trim, OCR, background removal, PDF tools) and floating file shelves. Everything runs locally.
Tags: [converter, file-converter, image-converter, video-converter, pdf, productivity, drag-and-drop]
ReleaseNotesUrl: https://github.com/thisisrishi/Tangelo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.9.0
"@ | Set-Content "$winget\thisisrishi.Tangelo.locale.en-US.yaml"

$scoop = [ordered]@{
  version = $Version
  description = 'Convert, edit and move files right where you drag them.'
  homepage = 'https://github.com/thisisrishi/Tangelo'
  license = 'MIT'
  architecture = @{ '64bit' = @{ url = "$base/$zip"; hash = $zipSha } }
  bin = 'Tangelo.exe'
  shortcuts = @(, @('Tangelo.exe', 'Tangelo'))
  persist = @()
  checkver = 'github'
  autoupdate = @{ architecture = @{ '64bit' = @{ url = 'https://github.com/thisisrishi/Tangelo/releases/download/v$version/Tangelo-windows-x64-portable.zip' } } }
}
$scoop | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'tangelo.json')
Remove-Item $tmp -Recurse -Force
Write-Host "Manifests written to $out"
