param([string]$VsRoot='C:/Program Files/Microsoft Visual Studio/2022/Community')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force $build | Out-Null
$batch=Join-Path $build 'probe-build.cmd'
$lines=@('@echo off',('call "'+$VsRoot+'\VC\Auxiliary\Build\vcvars64.bat"'),'if errorlevel 1 exit /b 1',('cd /d "'+$build+'"'),('cl /nologo /std:c++17 /EHsc /W4 /O2 /MT /DUNICODE /D_UNICODE "'+$root+'\native\BonProbe.cpp" /Fe:BonProbe.exe'),'exit /b %errorlevel%')
[IO.File]::WriteAllLines($batch,$lines,[Text.Encoding]::Default)
& $env:ComSpec /d /c $batch
if($LASTEXITCODE -ne 0){throw 'BonProbe build failed'}
