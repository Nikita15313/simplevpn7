param([string]$ReferenceDirectory, [string]$OutputDirectory, [string]$CorePath)
$ErrorActionPreference = 'Stop'
$drainRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ReferenceDirectory) { $ReferenceDirectory = Join-Path (Split-Path -Parent $drainRoot) 'net35-study\refs\build\.NETFramework\v3.5' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $drainRoot) 'core-drain-check' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& (Join-Path $drainRoot 'build.ps1') -ReferenceDirectory $ReferenceDirectory -OutputDirectory $OutputDirectory
$drainExecutable = Join-Path $OutputDirectory 'CoreDrainTests.exe'
$drainArguments = @('/nologo','/noconfig','/nostdlib+','/target:exe','/platform:x64','/langversion:5','/utf8output','/codepage:65001',('/out:' + $drainExecutable))
foreach ($drainReference in @('mscorlib.dll','System.dll','System.Core.dll')) { $drainArguments += '/r:' + (Join-Path $ReferenceDirectory $drainReference) }
$drainArguments += '/r:' + (Join-Path $OutputDirectory 'SimpleVPN.exe')
$drainArguments += Join-Path $drainRoot 'Tests\CoreDrainTests.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @drainArguments
if ($LASTEXITCODE -ne 0) { throw 'Core drain tests compilation failed.' }
Copy-Item -LiteralPath (Join-Path $drainRoot 'SimpleVPN.exe.config') -Destination ($drainExecutable + '.config')
$drainRunArguments = @()
if ($CorePath) { $drainRunArguments += [IO.Path]::GetFullPath($CorePath) }
& $drainExecutable @drainRunArguments | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Core drain tests failed.' }
