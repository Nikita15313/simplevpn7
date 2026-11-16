param([string]$ReferenceDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$buildRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ReferenceDirectory) { $ReferenceDirectory = Join-Path (Split-Path -Parent $buildRoot) 'net35-study\refs\build\.NETFramework\v3.5' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $buildRoot 'bin' }
$ReferenceDirectory = [IO.Path]::GetFullPath($ReferenceDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$arguments = @('/nologo','/noconfig','/nostdlib+','/target:winexe','/platform:x64','/langversion:5','/optimize+','/utf8output','/codepage:65001',
    ('/out:' + (Join-Path $OutputDirectory 'SimpleVPN.exe')),('/win32manifest:' + (Join-Path $buildRoot 'app.manifest')),('/win32icon:' + (Join-Path $buildRoot 'app.ico')))
foreach ($reference in @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll','System.Security.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
    $path = Join-Path $ReferenceDirectory $reference
    if (-not (Test-Path -LiteralPath $path)) { throw ('Missing .NET3.5 reference: ' + $reference) }
    $arguments += '/r:' + $path
}
$arguments += @(Get-ChildItem -LiteralPath $buildRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'The .NET3.5 fallback build failed.' }
Copy-Item -LiteralPath (Join-Path $buildRoot 'SimpleVPN.exe.config') -Destination $OutputDirectory
Write-Output (Join-Path $OutputDirectory 'SimpleVPN.exe')
