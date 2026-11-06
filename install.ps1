$ErrorActionPreference = 'Stop'
$hostName = 'jp.local_smtp_gmail'
$extensionId = 'kkppekckhlkhcckkceooiahihlaondia'
$hostExe = Join-Path $PSScriptRoot 'LocalSmtpHost.exe'
$manifest = Join-Path $PSScriptRoot 'native-host.json'
$key = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\' + $hostName
if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) { throw 'LocalSmtpHost.exe が見つかりません。' }
$previousRoot = $null
if (Test-Path -LiteralPath $key) {
    $existing = (Get-Item -LiteralPath $key).GetValue('')
    if ($existing -and $existing -ne $manifest) {
        try {
            $oldManifest = Get-Content -LiteralPath $existing -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifestRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName($existing))
            $exeRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName([string]$oldManifest.path))
            if ($oldManifest.name -ne $hostName -or [System.IO.Path]::GetFileName([string]$oldManifest.path) -ne 'LocalSmtpHost.exe' -or $manifestRoot -ne $exeRoot -or $oldManifest.allowed_origins -notcontains "chrome-extension://$extensionId/") { throw '別のアプリの登録です。' }
            $previousRoot = $exeRoot
        } catch { throw '既存の連携先を確認できません。登録を変更せずに終了しました。' }
    }
}
$configPath = Join-Path $PSScriptRoot 'smtp.ini'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    $oldConfig = if ($previousRoot) { Join-Path $previousRoot 'smtp.ini' } else { $null }
    if ($oldConfig -and (Test-Path -LiteralPath $oldConfig -PathType Leaf)) {
        Copy-Item -LiteralPath $oldConfig -Destination $configPath
        $oldExe = Join-Path $previousRoot 'mailsend-go.exe'
        $newExe = Join-Path $PSScriptRoot 'mailsend-go.exe'
        if ((Test-Path -LiteralPath $oldExe -PathType Leaf) -and -not (Test-Path -LiteralPath $newExe -PathType Leaf)) {
            $text = [System.IO.File]::ReadAllText($configPath, [System.Text.Encoding]::UTF8)
            $firstSection = [regex]::Match($text, '(?m)^[ \t]*\[')
            $prefix = if ($firstSection.Success) { $text.Substring(0, $firstSection.Index) } else { $text }
            $match = [regex]::Match($prefix, '(?im)^[ \t]*mailsend_path[ \t]*=[ \t]*(mailsend-go\.exe)[ \t]*\r?$')
            if ($match.Success) {
                $value = $match.Groups[1]
                $text = $text.Substring(0, $value.Index) + $oldExe + $text.Substring($value.Index + $value.Length)
                [System.IO.File]::WriteAllText($configPath, $text.TrimStart([char]0xFEFF), [System.Text.UTF8Encoding]::new($true))
            } elseif (-not [regex]::IsMatch($prefix, '(?im)^[ \t]*mailsend_path[ \t]*=')) {
                $text = 'mailsend_path=' + $oldExe + [Environment]::NewLine + $text.TrimStart([char]0xFEFF)
                [System.IO.File]::WriteAllText($configPath, $text, [System.Text.UTF8Encoding]::new($true))
            }
        }
        Write-Host '以前の smtp.ini を引き継ぎました。'
    } else {
    $template = Join-Path $PSScriptRoot 'smtp.example.ini'
    if (-not (Test-Path -LiteralPath $template -PathType Leaf)) { throw 'smtp.ini のひな形が見つかりません。' }
    Copy-Item -LiteralPath $template -Destination $configPath
    Write-Host '初回用の smtp.ini を作成しました。'
    }
}
$json = @{name=$hostName;description='Local SMTP Gmail bridge';path=$hostExe;type='stdio';allowed_origins=@("chrome-extension://$extensionId/")} | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($manifest, $json, [System.Text.UTF8Encoding]::new($false))
New-Item -Path $key -Force | Out-Null
Set-Item -LiteralPath $key -Value $manifest
Write-Host 'Windows側の登録が完了しました。'
Write-Host "Chrome拡張機能ID: $extensionId"
Write-Host 'chrome://extensions で開発者モードを有効にし、extension フォルダーを読み込んでください。'
