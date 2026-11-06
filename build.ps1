$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET FrameworkのC#コンパイラーが見つかりません。' }
& $compiler /nologo /codepage:65001 /target:exe /r:System.Web.Extensions.dll "/out:$PSScriptRoot\LocalSmtpHost.exe" "$PSScriptRoot\native\LocalSmtpHost.cs" "$PSScriptRoot\native\SettingsStore.cs" "$PSScriptRoot\native\CredentialStore.cs"
if ($LASTEXITCODE -ne 0) { throw "コンパイル失敗: $LASTEXITCODE" }
Write-Host 'LocalSmtpHost.exe を作成しました。'
