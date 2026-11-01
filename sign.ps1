<#
  ============================================================================
  sign.ps1 — Authenticode 代码签名（彻底消除 SmartScreen「无法识别的应用」）
  ============================================================================
  根因：未签名的 exe 在双击运行时会触发 Windows Defender SmartScreen 的
        「Windows 已保护你的电脑 / 无法识别的应用」拦截。
  正解：对 exe 进行 Authenticode 数字签名。本脚本即为此而生，并已接入构建流程
        （build / setup 编译完成后自动调用），不再依赖任何 .bat 绕过或关闭系统设置。

  用法：
    # 1) 使用 PFX 证书（推荐用于正式发布，证书来自受公众信任的 CA）
    powershell -ExecutionPolicy Bypass -File sign.ps1 -Pfx .\my-cert.pfx -Password "****" -Path .

    # 2) 使用 Windows 证书存储中已有的代码签名证书（按指纹）
    powershell -ExecutionPolicy Bypass -File sign.ps1 -Thumbprint A1B2C3... -Path .

    # 3) 环境变量方式（CI / 避免明文密码）
    $env:CODESIGN_PFX = ".\my-cert.pfx"; $env:CODESIGN_PASSWORD = "****"
    powershell -ExecutionPolicy Bypass -File sign.ps1

  说明：
    - 找不到证书时仅给出警告并以退出码 0 结束，不会中断构建（未签名时行为与原来一致）。
    - 默认附加 RFC3161 时间戳（DigiCert），即使证书将来过期，签名依然长期有效。
    - 要彻底消除最终用户机器上的 SmartScreen 提示，证书必须来自受公众信任的 CA：
        推荐 EV 代码签名证书（即时建立信誉，首次分发即不再弹窗）；
        普通 OV/IV 代码签名证书会在一定下载量后逐步建立信誉。
      自签名证书仅用于本地验证签名流程，最终用户机器仍会提示（因为发布者不受信任）。
  ============================================================================
#>
param(
    [string]$Path = ".",
    [string]$Pfx = "",
    [string]$Password = "",
    [string]$Thumbprint = "",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$NoTimestamp
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $root $Path }

# --------------------------------------------------------------------------
# 1) 解析代码签名证书
# --------------------------------------------------------------------------
$cert = $null
$pfxPath = if ($Pfx) { $Pfx } elseif ($env:CODESIGN_PFX) { $env:CODESIGN_PFX } else { "" }
if ($pfxPath) {
    if (-not [System.IO.Path]::IsPathRooted($pfxPath)) { $pfxPath = Join-Path $root $pfxPath }
    if (-not (Test-Path -LiteralPath $pfxPath)) {
        Write-Host ("[sign] 找不到 PFX 文件: " + $pfxPath) -ForegroundColor Red; exit 1
    }
    $pw = if ($Password) { $Password } elseif ($env:CODESIGN_PASSWORD) { $env:CODESIGN_PASSWORD } else { "" }
    $sec = ConvertTo-SecureString -String $pw -AsPlainText -Force
    $cert = Get-PfxCertificate -FilePath $pfxPath -Password $sec
}
elseif ($Thumbprint) {
    $cert = Get-ChildItem -Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $Thumbprint }
}
else {
    # 自动在个人存储里找代码签名证书（按 OID 1.3.6.1.5.5.7.3.3，跨语言）
    $cert = Get-ChildItem -Path Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object {
                ($_.EnhancedKeyUsageList.Value -contains '1.3.6.1.5.5.7.3.3') -or
                ($_.EnhancedKeyUsageList -match 'Code Signing') -or
                ($_.EnhancedKeyUsageList -match '代码签名')
            } | Select-Object -First 1
}

if (-not $cert) {
    Write-Host "[sign] 未找到代码签名证书，跳过签名（构建继续）。" -ForegroundColor Yellow
    Write-Host "       若要签名，请提供 -Pfx/-Thumbprint，或设置 CODESIGN_PFX / CODESIGN_PASSWORD。" -ForegroundColor Yellow
    exit 0
}

Write-Host ("[sign] 使用证书: " + $cert.Subject + "  (" + $cert.Thumbprint + ")") -ForegroundColor Cyan

# --------------------------------------------------------------------------
# 2) 收集待签名文件（exe / dll）
# --------------------------------------------------------------------------
$files = @()
if (Test-Path -LiteralPath $Path -PathType Container) {
    $files = Get-ChildItem -LiteralPath $Path -Recurse -Include *.exe,*.dll -ErrorAction SilentlyContinue |
             ForEach-Object { $_.FullName }
}
elseif (Test-Path -LiteralPath $Path) {
    $files = @($Path)
}
else {
    $files = Get-ChildItem -Path $root -Filter $Path -Recurse -Include *.exe,*.dll -ErrorAction SilentlyContinue |
             ForEach-Object { $_.FullName }
}

if ($files.Count -eq 0) { Write-Host "[sign] 没有找到需要签名的文件。" -ForegroundColor Yellow; exit 0 }

# --------------------------------------------------------------------------
# 3) 逐个签名（SHA256 + 时间戳；时间戳失败则退回无时间戳）
# --------------------------------------------------------------------------
$ts = if ($NoTimestamp) { $null } else { $TimestampServer }
$ok = 0; $signedUntrusted = 0; $fail = 0

foreach ($f in $files) {
    $name = [System.IO.Path]::GetFileName($f)
    try {
        $params = @{ FilePath = $f; Certificate = $cert; HashAlgorithm = 'SHA256' }
        if ($ts) { $params['TimestampServer'] = $ts }
        $r = Set-AuthenticodeSignature @params
        if ($r.Status -eq 'Valid') {
            Write-Host ("  [OK]   " + $name + "  （已签名且受信任）") -ForegroundColor Green; $ok++
        }
        elseif ($r.SignerCertificate) {
            Write-Host ("  [SIGN] " + $name + "  （已签名；当前为开发/自签名证书，本机未信任，使用 CA 证书后即受信任）") -ForegroundColor Yellow
            $signedUntrusted++
        }
        else {
            Write-Host ("  [WARN] " + $name + " -> " + $r.Status) -ForegroundColor Yellow; $fail++
        }
    }
    catch {
        # 时间戳服务不可达：退回无时间戳重签一次
        if ($ts -and ($_.Exception.Message -match 'timestamp|time stamp|时间戳')) {
            try {
                $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm 'SHA256'
                if ($r.SignerCertificate) {
                    Write-Host ("  [SIGN] " + $name + "  （已签名，无时间戳：时间戳服务不可达）") -ForegroundColor Yellow
                    $signedUntrusted++
                } else { Write-Host ("  [ERR]  " + $name + " : " + $r.Status) -ForegroundColor Red; $fail++ }
            } catch {
                Write-Host ("  [ERR]  " + $name + " : " + $_.Exception.Message) -ForegroundColor Red; $fail++
            }
        }
        else {
            Write-Host ("  [ERR]  " + $name + " : " + $_.Exception.Message) -ForegroundColor Red; $fail++
        }
    }
}

Write-Host ("[sign] 完成：受信任签名 " + $ok + "，已签名(未受信任) " + $signedUntrusted + "，异常 " + $fail) -ForegroundColor Cyan
exit $fail


