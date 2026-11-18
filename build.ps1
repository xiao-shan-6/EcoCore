# EcoCore 构建脚本
# 用法：右键“使用 PowerShell 运行”，或  pwsh -File build.ps1
# 产物：dist\EcoCore.exe（需要管理员权限运行，双击会弹 UAC）

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 某些受限环境里 dotnet 写不了 %USERPROFILE%\.dotnet，这里把 CLI 家目录指到工程内
$env:DOTNET_CLI_HOME  = Join-Path $root ".dotnet"
$env:NUGET_PACKAGES   = Join-Path $root ".nuget"
$env:DOTNET_NOLOGO    = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$proj = Join-Path $root "src\EcoCore\EcoCore.csproj"
if (-not (Test-Path $proj)) { throw "找不到工程文件: $proj" }

Write-Host "== 构建 Release x64 ==" -ForegroundColor Cyan
dotnet build $proj -c Release -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }

$bin = Join-Path $root "src\EcoCore\bin\x64\Release\net10.0-windows"
if (-not (Test-Path (Join-Path $bin "EcoCore.exe"))) { throw "构建产物不存在: $bin\EcoCore.exe" }

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# 这个环境里目录删除会被拒绝（dist 目录上有 Deny DeleteSubdirectoriesAndFiles），
# 所以不整体清空 dist：能删的旧文件删掉，删不掉的先改名挪到 unpack\_trash。
$trash = Join-Path $root "unpack\_trash"
Get-ChildItem $dist -File -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item $_.FullName -Force -ErrorAction Stop }
    catch {
        New-Item -ItemType Directory -Path $trash -Force | Out-Null
        try { Move-Item $_.FullName (Join-Path $trash ($_.Name + ".old")) -Force -ErrorAction Stop } catch { }
    }
}

Get-ChildItem $bin -File | Where-Object { $_.Name -notmatch '\.(pdb|txt|log)$' } | ForEach-Object {
    $src = $_.FullName
    $name = $_.Name                       # 先存下来：进了 catch，$_ 会变成 ErrorRecord
    $dst = Join-Path $dist $name
    if (Test-Path $dst) {
        $same = (Get-FileHash $src -Algorithm SHA256).Hash -eq (Get-FileHash $dst -Algorithm SHA256).Hash
        if ($same) { return }                      # 内容一致就不动，避免撞上占用
    }
    try { Copy-Item $src -Destination $dist -Force -ErrorAction Stop }
    catch {
        # 目标被占用（比如程序还开着）：改名挪走再复制，改名对已打开的文件通常还能成功
        try {
            New-Item -ItemType Directory -Path $trash -Force | Out-Null
            if (Test-Path $dst) {
                Move-Item $dst (Join-Path $trash ($name + "." + (Get-Date -Format 'HHmmss') + ".old")) -Force -ErrorAction Stop
            }
            Copy-Item $src -Destination $dist -Force -ErrorAction Stop
            Write-Host ("旧文件被占用，已改名挪走后重新复制：" + $name)
        }
        catch { Write-Warning ("跳过 " + $name + "：" + $_.Exception.Message) }
    }
}

# 提权自检批处理：双击即弹 UAC，跑完把结果写到同目录 selftest.log
$selftestCmd = @'
@echo off
cd /d "%~dp0"
echo 正在以管理员身份启动自检，请在弹出的 UAC 窗口点「是」...
powershell -NoProfile -Command "Start-Process -FilePath '%~dp0EcoCore.exe' -ArgumentList '--selftest' -Verb RunAs -Wait"
if exist "%~dp0selftest.log" (
  echo.
  echo ===== selftest.log =====
  type "%~dp0selftest.log"
) else (
  echo 没找到 selftest.log，可能被取消了授权。
)
echo.
pause
'@
Set-Content -Path (Join-Path $dist "自检.cmd") -Value $selftestCmd -Encoding Default

Copy-Item (Join-Path $root "README.md") -Destination (Join-Path $dist "README.md") -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "== 完成 ==" -ForegroundColor Green
Write-Host ("产物目录: " + $dist)
Write-Host ("主程序:   " + (Join-Path $dist "EcoCore.exe"))
Write-Host ""
Write-Host "提示：exe 清单声明了 requireAdministrator，双击会弹 UAC，允许后即为管理员权限。" -ForegroundColor Yellow
Write-Host "      进程绑核 / EcoQoS 其实不要管理员也能生效；要改频率上限 / 睿频 / EPP / 电源滑块就必须提权。" -ForegroundColor Yellow
