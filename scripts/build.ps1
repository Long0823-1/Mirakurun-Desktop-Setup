param([string]$VsRoot='C:/Program Files/Microsoft Visual Studio/2022/Community')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build-probe.ps1') -VsRoot $VsRoot
dotnet build (Join-Path $root 'src/MirakurunDesktopSetup.csproj') -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Managed build failed'}
$release=Join-Path $root 'build/release'
New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item (Join-Path $root 'src/bin/Release/net48/Mirakurun-Desktop-Setup.exe') $release
Copy-Item (Join-Path $root 'src/bin/Release/net48/Mirakurun-Desktop-Setup.exe.config') $release
foreach($file in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','catalog.json')){Copy-Item (Join-Path $root $file) $release}
Copy-Item (Join-Path $root 'docs') $release -Recurse -Force
Write-Output "Release: $release"
