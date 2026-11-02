# FocusKeep 构建脚本
# 依赖：Windows 自带的 .NET Framework 编译器（csc.exe），无需安装 SDK
param([switch]$Clean)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$outDir = Join-Path $root 'bin'

if ($Clean -and (Test-Path $outDir)) { Remove-Item $outDir -Recurse -Force }
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# 64 位优先，回退到 32 位
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw "找不到 csc.exe，请确认已安装 .NET Framework 4.x" }

$outExe = Join-Path $outDir 'FocusKeep.exe'
$manifest = Join-Path $srcDir 'app.manifest'
$appIcon = Join-Path $srcDir 'app.ico'
$resDir = Join-Path $srcDir 'res'
$sources = Get-ChildItem -Path (Join-Path $srcDir '*.cs') | ForEach-Object { $_.FullName }

# 四张状态图标编译进 exe（内嵌资源名 FocusKeep.res.<文件名>）
$resources = @(
    'icon-on.png', 'icon-on-lock.png', 'icon-off.png', 'icon-off-lock.png'
) | ForEach-Object { "/resource:$(Join-Path $resDir $_),FocusKeep.res.$_" }

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/codepage:65001',          # 源码是 UTF-8，不加这句中文会乱码
    "/win32manifest:$manifest",
    "/win32icon:$appIcon",
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/out:$outExe"
) + $resources + $sources

Write-Host "csc : $csc"
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }

$file = Get-Item $outExe
Write-Host ""
Write-Host ("OK  {0}  ({1:N0} 字节)" -f $file.FullName, $file.Length) -ForegroundColor Green
