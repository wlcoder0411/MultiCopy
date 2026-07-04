#requires -Version 5.1
<#
.SYNOPSIS
  MultiCopy 一键发布脚本：生成单文件自包含 exe + 绿色版 zip + Inno Setup 安装包

.DESCRIPTION
  Step 1: dotnet publish 生成单文件 exe 到 publish/win-x64/
  Step 2: 压缩成 dist/MultiCopy-{version}-win-x64.zip（绿色版）
  Step 3: 调用 Inno Setup ISCC.exe 生成 dist/MultiCopySetup-{version}.exe（安装包，需装 Inno Setup 6）

.NOTES
  前置条件：
  - .NET 8 SDK（已安装）
  - 可选：Inno Setup 6（从 https://jrsoftware.org/isdl.php 下载安装）
#>

[CmdletBinding()]
param(
    [switch]$SkipInstaller  # 跳过安装包生成（仅产出绿色版 zip）
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csproj = Join-Path $root 'src\MultiCopy\MultiCopy.csproj'
$pubProfile = 'win-x64-selfcontained'
$publishDir = Join-Path $root 'publish\win-x64'
$distDir = Join-Path $root 'dist'
$issFile = Join-Path $root 'installer\MultiCopy.iss'

function Write-Step($msg) { Write-Host "`n========== $msg ==========" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Info($msg) { Write-Host "  [i]  $msg" -ForegroundColor Gray }
function Write-Warn($msg) { Write-Host "  [!]  $msg" -ForegroundColor Yellow }
function Format-Size($bytes) {
    if ($bytes -ge 1MB) { '{0:N2} MB' -f ($bytes / 1MB) }
    elseif ($bytes -ge 1KB) { '{0:N2} KB' -f ($bytes / 1KB) }
    else { "$bytes B" }
}

# ---------- 读取版本号 ----------
Write-Step '读取版本号'
[xml]$csprojXml = Get-Content $csproj -Encoding UTF8
$version = $csprojXml.Project.PropertyGroup.Version
if (-not $version) { $version = '3.6' }
Write-OK "版本号: $version"

# ---------- 清理旧产物 ----------
Write-Step '清理旧产物'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force; Write-Info '已清理 publish/win-x64/' }
if (Test-Path $distDir)    { Remove-Item $distDir -Recurse -Force;    Write-Info '已清理 dist/' }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Write-OK 'dist/ 目录已就绪'

# ---------- Step 1: dotnet publish ----------
Write-Step 'Step 1/3 · dotnet publish（单文件自包含 exe）'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# 先停止可能在运行的 MultiCopy 进程（避免 exe 文件被占用）
Get-Process -Name MultiCopy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Info '已停止运行中的 MultiCopy 进程（如有）'

$pubArgs = @('publish', $csproj, '-p:PublishProfile=' + $pubProfile, '--verbosity', 'minimal')
& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
$sw.Stop()
Write-OK "发布完成，耗时 $([math]::Round($sw.Elapsed.TotalSeconds, 1)) 秒"

$exePath = Join-Path $publishDir 'MultiCopy.exe'
if (-not (Test-Path $exePath)) { throw "未找到产物: $exePath" }
$exeSize = (Get-Item $exePath).Length
Write-Info "产物: $exePath"
Write-Info "大小: $(Format-Size $exeSize)"

# 列出 publish 目录所有文件（验证是否为单文件）
$pubFiles = Get-ChildItem $publishDir -File
Write-Info "publish/win-x64/ 下文件数: $($pubFiles.Count)"
if ($pubFiles.Count -gt 3) {
    Write-Warn '产物含多个文件（可能未成功打包为单文件），请检查 pubxml 配置'
}

# ---------- Step 2: 压缩绿色版 zip ----------
Write-Step 'Step 2/3 · 打包绿色版 zip'
$zipName = "MultiCopy-$version-win-x64.zip"
$zipPath = Join-Path $distDir $zipName
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$sw2.Stop()
$zipSize = (Get-Item $zipPath).Length
Write-OK "绿色版 zip 已生成，耗时 $([math]::Round($sw2.Elapsed.TotalSeconds, 1)) 秒"
Write-Info "产物: $zipPath"
Write-Info "大小: $(Format-Size $zipSize)"

# ---------- Step 3: Inno Setup 安装包 ----------
Write-Step 'Step 3/3 · Inno Setup 安装包'

if ($SkipInstaller) {
    Write-Warn '已通过 -SkipInstaller 跳过安装包生成'
}
else {
    # 查找 ISCC.exe：PATH 中 or 默认安装路径
    $iscc = $null
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
    else {
        $defaultPaths = @(
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe',
            'D:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'D:\Program Files\Inno Setup 6\ISCC.exe',
            'E:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'E:\Program Files\Inno Setup 6\ISCC.exe',
            'E:\ProgramFiles\Inno Setup 6\ISCC.exe',
            'F:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'F:\Program Files\Inno Setup 6\ISCC.exe',
            'C:\Program Files (x86)\Inno Setup 5\ISCC.exe'
        )
        foreach ($p in $defaultPaths) {
            if (Test-Path $p) { $iscc = $p; break }
        }
    }

    if (-not $iscc) {
        Write-Warn '未找到 Inno Setup（ISCC.exe），已跳过安装包生成'
        Write-Info '如需生成安装包，请从 https://jrsoftware.org/isdl.php 下载安装 Inno Setup 6（免费）'
        Write-Info '安装后重新运行此脚本，或手动编译 installer\MultiCopy.iss'
    }
    elseif (-not (Test-Path $issFile)) {
        Write-Warn "未找到 Inno Setup 脚本: $issFile"
    }
    else {
        Write-Info "使用 ISCC: $iscc"
        $sw3 = [System.Diagnostics.Stopwatch]::StartNew()
        # 用 /D 传入版本号和源目录（iss 中用 {#AppVersion} 引用）
        & $iscc /Q /DAppVersion="$version" /DSourceExe="$exePath" /DDistDir="$distDir" $issFile
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "ISCC 编译失败（退出码 $LASTEXITCODE），请检查 $issFile"
        }
        else {
            $sw3.Stop()
            $setupName = "MultiCopySetup-$version.exe"
            $setupPath = Join-Path $distDir $setupName
            if (Test-Path $setupPath) {
                $setupSize = (Get-Item $setupPath).Length
                Write-OK "安装包已生成，耗时 $([math]::Round($sw3.Elapsed.TotalSeconds, 1)) 秒"
                Write-Info "产物: $setupPath"
                Write-Info "大小: $(Format-Size $setupSize)"
            }
        }
    }
}

# ---------- 产物清单 ----------
Write-Step '发布完成 · 产物清单'
Write-Host "  版本: $version`n" -ForegroundColor White
$artifacts = @(
    @{ Name = '绿色版 zip';     Path = $zipPath },
    @{ Name = '单文件 exe（在 zip 内）'; Path = $exePath }
)
$setupPath = Join-Path $distDir "MultiCopySetup-$version.exe"
if (Test-Path $setupPath) { $artifacts += @{ Name = '安装包'; Path = $setupPath } }

foreach ($a in $artifacts) {
    if (Test-Path $a.Path) {
        $size = (Get-Item $a.Path).Length
        Write-Host ("  {0,-20} {1,-60} {2}" -f $a.Name, $a.Path, (Format-Size $size)) -ForegroundColor Green
    }
}

Write-Host "`n  发给别人时:" -ForegroundColor Cyan
Write-Host "    - 给普通用户 → 发安装包 MultiCopySetup-$version.exe（双击安装，自动创建快捷方式）" -ForegroundColor Gray
Write-Host "    - 给免安装用户 → 发绿色版 zip（解压后双击 MultiCopy.exe 即可）" -ForegroundColor Gray
Write-Host ""
