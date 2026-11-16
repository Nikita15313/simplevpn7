<#
.SYNOPSIS
Builds and runs .NET 3.5 parser, recovery, settings, diagnostics, title and optional core lifecycle/preparation tests.
.DESCRIPTION
Default: parser, recovery with a fake proxy backend, same-user activation,
settings, interval migration, sanitized diagnostics and subscription-title checks.
CorePath adds real Xray configuration, preparation and lifecycle checks with the
hidden core, fake HTTPS checks and fake Windows proxy settings.
No GUI opens, external network requests run, or Windows proxy/startup settings
change. Files use real DPAPI for the current Windows user. No real subscription
input is accepted or copied into source. All credentials in fixtures are synthetic.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests.ps1 -ReferenceDirectory C:\refs\v3.5
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests.ps1 -ReferenceDirectory C:\refs\v3.5 -CorePath C:\SimpleVPN\app\core\xray.exe
#>
[CmdletBinding()]
param(
    [string]$ReferenceDirectory,
    [string]$CorePath,
    [string]$ResultsDirectory,
    [string]$ScratchDirectory
)
$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ReferenceDirectory) { $ReferenceDirectory = Join-Path (Split-Path -Parent $testRoot) 'net35-study\refs\build\.NETFramework\v3.5' }
$ReferenceDirectory = [IO.Path]::GetFullPath($ReferenceDirectory)
if ($CorePath) {
    if (-not (Test-Path -LiteralPath $CorePath -PathType Leaf)) { throw 'CorePath must identify an existing xray.exe.' }
    $CorePath = (Get-Item -LiteralPath $CorePath).FullName
}
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $testRoot 'Tests\results' }
if (-not $ScratchDirectory) { $ScratchDirectory = Join-Path ([IO.Path]::GetTempPath()) ('SVPN35-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$ScratchDirectory = [IO.Path]::GetFullPath($ScratchDirectory)
$testRun = Join-Path $ResultsDirectory ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$testBin = Join-Path $testRoot 'Tests\bin'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $testRun | Out-Null
New-Item -ItemType Directory -Force -Path $testBin | Out-Null
& (Join-Path $testRoot 'build.ps1') -ReferenceDirectory $ReferenceDirectory
$application = Join-Path $testRoot 'bin\SimpleVPN.exe'
Copy-Item -LiteralPath $application -Destination $testBin

$compileBase = @('/nologo','/noconfig','/nostdlib+','/target:exe','/platform:x64','/langversion:5','/utf8output','/codepage:65001')
foreach ($testReference in @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll','System.Security.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
    $compileBase += '/r:' + (Join-Path $ReferenceDirectory $testReference)
}

function Invoke-Check {
    param([string]$Name, [string[]]$CheckArguments, [switch]$WithApplicationSources)
    $testExecutable = Join-Path $testBin ($Name + '.exe')
    $compileArguments = @($compileBase)
    $compileArguments += '/out:' + $testExecutable
    if ($WithApplicationSources) {
        $compileArguments += '/main:' + $Name
        $compileArguments += @(Get-ChildItem -LiteralPath $testRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
    } else {
        $compileArguments += '/r:' + $application
    }
    $compileArguments += Join-Path $testRoot ('Tests\' + $Name + '.cs')
    & $compiler @compileArguments
    if ($LASTEXITCODE -ne 0) { throw ('Test compilation failed: ' + $Name) }
    Copy-Item -LiteralPath (Join-Path $testRoot 'SimpleVPN.exe.config') -Destination ($testExecutable + '.config')
    $testLines = @(& $testExecutable @CheckArguments 2>&1)
    $testExitCode = $LASTEXITCODE
    $testText = @($testLines | ForEach-Object { [string]$_ })
    [IO.File]::WriteAllLines((Join-Path $testRun ($Name + '.txt')), [string[]]$testText, [Text.Encoding]::UTF8)
    $testText | ForEach-Object { Write-Output $_ }
    if ($testExitCode -ne 0) { throw ($Name + ' failed. Report directory: ' + $testRun) }
}

$parserArguments = @()
if ($CorePath) { $parserArguments += $CorePath }
Invoke-Check -Name 'SubscriptionTests' -CheckArguments $parserArguments
Invoke-Check -Name 'RecoveryTests' -CheckArguments @((Join-Path $ScratchDirectory 'r'))
Invoke-Check -Name 'ActivationTests' -CheckArguments @($application)
Invoke-Check -Name 'OptionsTests' -CheckArguments $parserArguments
Invoke-Check -Name 'IntervalTests' -CheckArguments @((Join-Path $ScratchDirectory 'i'))
Invoke-Check -Name 'DiagnosticsTests' -CheckArguments @((Join-Path $ScratchDirectory 'd'))
Invoke-Check -Name 'SubscriptionTitleTests' -WithApplicationSources
Invoke-Check -Name 'CoreDrainTests' -CheckArguments $parserArguments
Invoke-Check -Name 'UpdateTests' -CheckArguments @((Join-Path $ScratchDirectory 'u'))
Invoke-Check -Name 'PendingUpdateTests' -CheckArguments @((Join-Path $ScratchDirectory 'pending'))
Invoke-Check -Name 'UpdateTransportTests' -WithApplicationSources
Invoke-Check -Name 'ConfigSourceTests' -CheckArguments @((Join-Path $ScratchDirectory 'sources'))
Invoke-Check -Name 'XhttpExtraTests' -CheckArguments $parserArguments
Invoke-Check -Name 'UiDataTests' -WithApplicationSources
if ($CorePath) {
    Invoke-Check -Name 'CorePreparationTests' -CheckArguments @($CorePath, (Join-Path $ScratchDirectory 'p'))
    Invoke-Check -Name 'PreparedControllerTests' -CheckArguments @($CorePath, (Join-Path $ScratchDirectory 'pc'))
    Invoke-Check -Name 'LifecycleTests' -CheckArguments @($CorePath, (Join-Path $ScratchDirectory 'l'))
} else {
    Write-Output 'Xray fixtures and lifecycle tests skipped: supply -CorePath to enable them.'
}
Write-Output ('All requested tests passed. Reports: ' + $testRun)
