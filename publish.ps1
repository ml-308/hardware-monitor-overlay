<#
================================================================
 一键发布到 GitHub：创建仓库 → 推送代码 → 创建 Release → 上传产物
================================================================
 用法（需要能访问 github.com 的网络）：
   1) 生成 Personal Access Token（勾选 repo 权限）
      https://github.com/settings/tokens
   2) 设置环境变量并执行：
        $env:GITHUB_TOKEN = "ghp_xxxxxxxx"
        powershell -ExecutionPolicy Bypass -File publish.ps1 -Owner 你的用户名
   3) 仓库默认名 hardware-monitor-overlay，可用 -Repo 改名

 说明：脚本会把 token 临时写入 git remote URL 以完成推送，推送结束后
       会立即把 remote 改回不带 token 的地址。
#>
param(
    [string]$Owner = $env:GITHUB_USER,
    [string]$Repo  = "hardware-monitor-overlay",
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Tag   = "v1.0.0",
    [switch]$Private
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not $Token) { Write-Host "[错误] 缺少 GitHub Token：请先设置 GITHUB_TOKEN 环境变量" -ForegroundColor Red; exit 1 }

$headers = @{
    Authorization = "token $Token"
    Accept        = "application/vnd.github+json"
    "User-Agent"  = "HwMon-Publisher"
}

if (-not $Owner) {
    Write-Host "[0/5] 读取账号信息 ..."
    $me = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/user"
    $Owner = $me.login
}
Write-Host "目标仓库: $Owner/$Repo   标签: $Tag"

Write-Host "[1/5] 创建远程仓库（已存在则跳过） ..."
try {
    $body = @{ name = $Repo; description = "轻量透明可拖拽可缩放的 Windows 硬件利用率浮窗（CPU/内存/GPU/磁盘/网络）"; private = [bool]$Private; has_issues = $true; has_wiki = $false } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Headers $headers -Uri "https://api.github.com/user/repos" -Body $body -ContentType "application/json" | Out-Null
    Write-Host "      已创建 https://github.com/$Owner/$Repo"
} catch {
    Write-Host "      仓库已存在或创建失败，继续尝试推送: $($_.Exception.Message)"
}

Write-Host "[2/5] 初始化本地仓库并提交 ..."
if (-not (Test-Path ".git")) { git init | Out-Null }
git add -A
git -c user.name="HwMon" -c user.email="hwmon@users.noreply.github.com" commit -m "release: v1.0.0 - 轻量硬件利用率浮窗（透明/可拖动/可缩放）" 2>$null | Out-Null
git branch -M main

Write-Host "[3/5] 推送代码与标签 ..."
git remote remove origin 2>$null | Out-Null
git remote add origin "https://$Token@github.com/$Owner/$Repo.git"
git push -u origin main --force
git tag -f $Tag | Out-Null
git push origin $Tag --force
git remote set-url origin "https://github.com/$Owner/$Repo.git"

Write-Host "[4/5] 创建 Release ..."
$notes = ""
if (Test-Path "RELEASE_NOTES.md") { $notes = Get-Content "RELEASE_NOTES.md" -Raw }
$relBody = @{ tag_name = $Tag; name = "硬件监控浮窗 $Tag"; body = $notes; draft = $false; prerelease = $false } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Headers $headers -Uri "https://api.github.com/repos/$Owner/$Repo/releases" -Body $relBody -ContentType "application/json"
Write-Host "      Release: $($release.html_url)"

Write-Host "[5/5] 上传发布产物 ..."
$files = @("HardwareMonitor.exe", "setup.exe", "release\HardwareMonitor-1.0.0-portable.zip", "release\SHA256SUMS.txt")
foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Host "      跳过（不存在）: $f" -ForegroundColor Yellow; continue }
    $name = Split-Path $f -Leaf
    $url = "https://uploads.github.com/repos/$Owner/$Repo/releases/$($release.id)/assets?name=$name"
    try {
        Invoke-RestMethod -Method Post -Headers $headers -Uri $url -InFile $f -ContentType "application/octet-stream" | Out-Null
        Write-Host "      已上传 $name"
    } catch {
        Write-Host "      上传失败 $name : $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "发布完成：" -ForegroundColor Green
Write-Host "  仓库    https://github.com/$Owner/$Repo"
Write-Host "  Release https://github.com/$Owner/$Repo/releases/tag/$Tag"
