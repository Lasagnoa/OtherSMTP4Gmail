$ErrorActionPreference = 'Stop'
$hostName = 'jp.local_smtp_gmail'
$extensionId = 'kkppekckhlkhcckkceooiahihlaondia'
$hostExe = Join-Path $PSScriptRoot 'LocalSmtpHost.exe'
$manifest = Join-Path $PSScriptRoot 'native-host.json'
$key = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\' + $hostName
if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) { throw 'LocalSmtpHost.exe が見つかりません。' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'smtp.ini') -PathType Leaf)) {
    $template = Join-Path $PSScriptRoot 'smtp.example.ini'
    if (-not (Test-Path -LiteralPath $template -PathType Leaf)) { throw 'smtp.ini のひな形が見つかりません。' }
    Copy-Item -LiteralPath $template -Destination (Join-Path $PSScriptRoot 'smtp.ini')
    Write-Host '初回用の smtp.ini を作成しました。'
}
if (Test-Path -LiteralPath $key) {
    $existing = (Get-Item -LiteralPath $key).GetValue('')
    if ($existing -and $existing -ne $manifest) { throw "同名のNative Messaging設定が存在します: $existing" }
}
$json = @{name=$hostName;description='Local SMTP Gmail bridge';path=$hostExe;type='stdio';allowed_origins=@("chrome-extension://$extensionId/")} | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($manifest, $json, [System.Text.UTF8Encoding]::new($false))
New-Item -Path $key -Force | Out-Null
Set-Item -LiteralPath $key -Value $manifest
Write-Host 'Windows側の登録が完了しました。'
Write-Host "Chrome拡張機能ID: $extensionId"
Write-Host 'chrome://extensions で開発者モードを有効にし、extension フォルダーを読み込んでください。'
