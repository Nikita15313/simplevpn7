param(
    [string]$ReferenceDirectory,
    [string]$CoreDirectory,
    [string]$PackageDirectory,
    [string]$CompilerDirectory = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Tools\MSVC\14.29.30133\bin\Hostx64\x64',
    [string]$SdkDirectory = 'C:\Program Files (x86)\Windows Kits\10',
    [string]$SdkVersion = '10.0.19041.0'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $CoreDirectory) { $CoreDirectory = Join-Path (Split-Path -Parent $sourceRoot) 'vendor\xray-win7-64-v26.3.27' }
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $sourceRoot 'package' }
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if (Test-Path -LiteralPath $PackageDirectory) { throw 'PackageDirectory already exists. Choose a new directory; existing files/data are never overwritten.' }
$corePath = Join-Path $CoreDirectory 'xray.exe'
if ((Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash -ne 'DD7B2E94FEF71C56E34CCEF0420D58162C93E2223B1A3DD126DB0DDF47D48FDD') { throw 'The supplied Xray core does not match the verified Win7 release.' }
& (Join-Path $sourceRoot 'build.ps1') -ReferenceDirectory $ReferenceDirectory
$nativeOut = Join-Path $sourceRoot 'launcher\bin'
New-Item -ItemType Directory -Path $nativeOut -Force | Out-Null
$cl = Join-Path $CompilerDirectory 'cl.exe'
$link = Join-Path $CompilerDirectory 'link.exe'
$rc = Join-Path $SdkDirectory ('bin\' + $SdkVersion + '\x64\rc.exe')
$include = Join-Path $SdkDirectory ('Include\' + $SdkVersion)
$compilerInclude = [IO.Path]::GetFullPath((Join-Path $CompilerDirectory '..\..\..\include'))
$lib = Join-Path $SdkDirectory ('Lib\' + $SdkVersion + '\um\x64')
foreach ($required in @($cl, $link, $rc)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Missing native build tool: ' + $required) } }
Push-Location (Join-Path $sourceRoot 'launcher')
try {
    & $rc /nologo ('/fo' + (Join-Path $nativeOut 'Launcher.res')) Launcher.rc
    if ($LASTEXITCODE -ne 0) { throw 'Native resource build failed.' }
    & $cl /nologo /c /TC /O1 /GS- /Zl /W4 /WX /utf-8 ('/I' + $compilerInclude) ('/I' + (Join-Path $include 'ucrt')) ('/I' + (Join-Path $include 'um')) ('/I' + (Join-Path $include 'shared')) ('/Fo' + (Join-Path $nativeOut 'Launcher.obj')) Launcher.c
    if ($LASTEXITCODE -ne 0) { throw 'Native launcher compilation failed.' }
    & $link /nologo /NODEFAULTLIB /ENTRY:Start /SUBSYSTEM:WINDOWS,6.01 /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /MANIFEST:NO ('/LIBPATH:' + $lib) ('/OUT:' + (Join-Path $nativeOut 'SimpleVPN.exe')) (Join-Path $nativeOut 'Launcher.obj') (Join-Path $nativeOut 'Launcher.res') kernel32.lib user32.lib advapi32.lib shell32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native launcher link failed.' }
} finally { Pop-Location }
$appDirectory = Join-Path $PackageDirectory 'app'
$coreOutput = Join-Path $appDirectory 'core'
New-Item -ItemType Directory -Path $coreOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeOut 'SimpleVPN.exe') -Destination $PackageDirectory
foreach ($name in @('SimpleVPN.exe','SimpleVPN.exe.config')) { Copy-Item -LiteralPath (Join-Path $sourceRoot ('bin\' + $name)) -Destination $appDirectory }
foreach ($name in @('xray.exe','geoip.dat','geosite.dat','LICENSE')) { Copy-Item -LiteralPath (Join-Path $CoreDirectory $name) -Destination $coreOutput }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'ОТКРОЙТЕ МЕНЯ.txt') -Destination $PackageDirectory
Copy-Item -LiteralPath (Join-Path $sourceRoot 'COMPATIBILITY.md') -Destination $appDirectory
Write-Output ('Package staged, no private data included: ' + $PackageDirectory)
