[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$SetupPath,
 [Parameter(Mandatory=$true)][string]$Version,
 [Parameter(Mandatory=$true)][string]$GhPath,
 [Parameter(Mandatory=$true)][string]$SigningKeyPath,
 [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$repository='Nikita15313/simplevpn7'
$versionObject=[Version]$Version
if($versionObject.Revision -lt 0){throw 'Use a four-part version, for example 3.0.0.0.'}
$setup=Get-Item -LiteralPath $SetupPath
if($setup.Length -lt 1024 -or $setup.Length -gt 128MB){throw 'Installer size is invalid.'}
$privacy=(& $GhPath repo view $repository --json isPrivate | ConvertFrom-Json).isPrivate
if(-not $privacy){throw 'Refusing publication: repository must remain private.'}
$tag='v'+$versionObject.ToString(3)
$notes='SimpleVPN 7 '+$versionObject.ToString(3)+'. Signed Windows 7 SP1 x64 release. Settings and subscription data are never included.'
& $GhPath release create $tag $setup.FullName --repo $repository --draft --title ('SimpleVPN 7 '+$versionObject.ToString(3)) --notes $notes
if($LASTEXITCODE -ne 0){throw 'Draft release creation failed. Existing releases are not overwritten.'}
$release=& $GhPath api ('repos/'+$repository+'/releases/tags/'+$tag) | ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Cannot read draft release.'}
$asset=@($release.assets | Where-Object name -eq $setup.Name)
if($asset.Count -ne 1){throw 'Expected exactly one installer asset.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$manifestPath=Join-Path $OutputDirectory 'update.json'
$signaturePath=Join-Path $OutputDirectory 'update.json.sig'
$manifest=[ordered]@{Version=$versionObject.ToString(4);Sha256=(Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash;AssetUrl=$asset[0].url;Size=$setup.Length}
$manifestBytes=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
[IO.File]::WriteAllBytes($manifestPath,$manifestBytes)
$keyBytes=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($SigningKeyPath),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider -ArgumentList (New-Object Security.Cryptography.CspParameters(24))
$rsa.PersistKeyInCsp=$false
try {
 $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($keyBytes))
 $signature=$rsa.SignData($manifestBytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
 [IO.File]::WriteAllText($signaturePath,[Convert]::ToBase64String($signature),[Text.Encoding]::ASCII)
} finally {[Array]::Clear($keyBytes,0,$keyBytes.Length);$rsa.Dispose()}
& $GhPath release upload $tag $manifestPath $signaturePath --repo $repository
if($LASTEXITCODE -ne 0){throw 'Signed metadata upload failed; release remains a draft.'}
& $GhPath release edit $tag --repo $repository --draft=false --latest
if($LASTEXITCODE -ne 0){throw 'Release publication failed.'}
Write-Output ('Published private signed release: https://github.com/'+$repository+'/releases/tag/'+$tag)
