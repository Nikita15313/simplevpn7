param(
    [string]$CompilerDirectory = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Tools\MSVC\14.29.30133\bin\Hostx64\x64',
    [string]$SdkDirectory = 'C:\Program Files (x86)\Windows Kits\10',
    [string]$SdkVersion = '10.0.19041.0',
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $sourceRoot ('Tests\results\launcher-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$compilerRoot = [IO.Path]::GetFullPath((Join-Path $CompilerDirectory '..\..\..'))
$compiler = Join-Path $CompilerDirectory 'cl.exe'
$source = Join-Path $sourceRoot 'Tests\LauncherJobTests.c'
$executable = Join-Path $ResultsDirectory 'LauncherJobTests.exe'
$arguments = @('/nologo','/TC','/MT','/O1','/W4','/WX','/utf-8')
foreach ($include in @(
    (Join-Path $compilerRoot 'include'),
    (Join-Path $SdkDirectory ('Include\' + $SdkVersion + '\ucrt')),
    (Join-Path $SdkDirectory ('Include\' + $SdkVersion + '\um')),
    (Join-Path $SdkDirectory ('Include\' + $SdkVersion + '\shared'))
)) { $arguments += '/I' + $include }
$arguments += @(('/Fo' + (Join-Path $ResultsDirectory 'LauncherJobTests.obj')), ('/Fe' + $executable), $source, '/link')
foreach ($library in @(
    (Join-Path $compilerRoot 'lib\x64'),
    (Join-Path $SdkDirectory ('Lib\' + $SdkVersion + '\ucrt\x64')),
    (Join-Path $SdkDirectory ('Lib\' + $SdkVersion + '\um\x64'))
)) { $arguments += '/LIBPATH:' + $library }
$arguments += @('kernel32.lib','user32.lib','shell32.lib','advapi32.lib')
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Launcher policy test compilation failed.' }
# The test calls the production policy with stubbed OS queries and exit handling.
# It creates no GUI, processes, jobs, or network connections.
$output = @(& $executable 2>&1)
$testExit = $LASTEXITCODE
$output | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'LauncherJobTests.txt') -Encoding UTF8
$output | Write-Output
if ($testExit -ne 0) { throw 'Launcher policy tests failed.' }
Write-Output ('Reports: ' + $ResultsDirectory)
