# 一键发布：编译成「单个 exe」（自包含运行时，目标机不用装 .NET）
# 用法：pwsh -File publish.ps1
# 产物：release\EcoCore.exe + release\EcoCore-win-x64.zip —— 双击即用，带 UAC 提权

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# dotnet CLI 的 home 必须在工程内，否则沙箱下写 %USERPROFILE%\.dotnet 会被拒绝
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet"
$env:NUGET_PACKAGES  = Join-Path $root ".nuget"
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

$outDir   = Join-Path $root "release"
$buildDir = Join-Path $outDir "_build"      # 先出到这里，再搬进 release
$proj     = Join-Path $root "src\EcoCore\EcoCore.csproj"
$exe      = Join-Path $outDir "EcoCore.exe"

Write-Host "== 发布单文件 exe ==" -ForegroundColor Cyan
if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

dotnet publish $proj `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:SatelliteResourceLanguages=zh-Hans `
    -o $buildDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$newExe = Join-Path $buildDir "EcoCore.exe"
if (-not (Test-Path $newExe)) { throw "发布目录里没有 EcoCore.exe" }

# 单文件校验：发布目录里除了 exe / pdb 不该再有多余的运行时文件
$extra = Get-ChildItem $buildDir -File | Where-Object { $_.Name -notin @("EcoCore.exe", "EcoCore.pdb") }
if ($extra) {
    Write-Warning ("单文件包里还有别的文件：" + ($extra.Name -join ", "))
} else {
    Write-Host "单文件校验通过：发布目录里只有 EcoCore.exe" -ForegroundColor Green
}

$newHash = (Get-FileHash $newExe -Algorithm SHA256).Hash

# ── 搬进 release ────────────────────────────────────────────────
# release\EcoCore.exe 经常正被用户运行的实例占用；占用时改名让路（同目录改名不受影响），
# 这一步决定了「发出去的到底是不是新代码」，不能默默跳过。
$stale = $false
if (Test-Path $exe) {
    $oldHash = (Get-FileHash $exe -Algorithm SHA256).Hash
    if ($oldHash -eq $newHash) {
        Write-Host "release 里的 exe 已是最新（哈希相同），跳过复制。" -ForegroundColor Green
        $stale = $false
    } else {
        try {
            Copy-Item $newExe $exe -Force -ErrorAction Stop
            Write-Host "已覆盖 release\EcoCore.exe" -ForegroundColor Green
        } catch {
            Write-Warning "release\EcoCore.exe 被占用（多半是正在运行的 EcoCore），改用改名让路……"
            $aside = Join-Path $outDir ("_old-EcoCore-{0}.exe" -f (Get-Date -Format "HHmmss"))
            try {
                Move-Item $exe $aside -ErrorAction Stop
                Copy-Item $newExe $exe -ErrorAction Stop
                Write-Host "已把旧 exe 改名成 $aside，并放入新 exe。" -ForegroundColor Green
            } catch {
                $stale = $true
                Write-Host ""
                Write-Warning "无法替换 release\EcoCore.exe：$($_.Exception.Message)"
                Write-Warning "请先关掉正在运行的 EcoCore.exe（托盘图标 → 退出），再重跑本脚本。"
                Write-Warning "本次不生成 zip —— 否则会把旧 exe 当成新版发出去。"
                Write-Host "新 exe 在：$newExe（$newHash）" -ForegroundColor Yellow
            }
        }
    }
} else {
    Copy-Item $newExe $exe -Force
    Write-Host "已放入 release\EcoCore.exe" -ForegroundColor Green
}

if ($stale) { exit 2 }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ""
Write-Host "主程序：  $exe"
Write-Host "大小：    $size MB"
Write-Host "SHA256：  $hash"

# 额外出一个 zip：解压出来的文件不带「来自 Internet」标记，SmartScreen 不会再拦
# （实测：Expand-Archive / ZipFile.ExtractToDirectory 都不会复制 Zone.Identifier）
Write-Host ""
Write-Host "== 打包 zip（免 SmartScreen 拦截的分发方式）==" -ForegroundColor Cyan
$zip = Join-Path $outDir "EcoCore-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$stage = Join-Path $env:TEMP ("ecocore-zip-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item $exe (Join-Path $stage "EcoCore.exe")
$helper = Join-Path $root "解除阻止.cmd"
if (Test-Path $helper) { Copy-Item $helper (Join-Path $stage "解除阻止.cmd") }
$readme = Join-Path $root "README.md"
if (Test-Path $readme) { Copy-Item $readme (Join-Path $stage "README.md") }

@"
EcoCore —— 大小核调度省电工具
================================
1) 双击 EcoCore.exe，UAC 点「是」
2) 如果仍被 SmartScreen 拦住：双击 解除阻止.cmd，然后再运行 EcoCore.exe
   （zip 解压出来的文件通常已经没有 Internet 标记，一般不会拦）
3) 想开机自动生效：打开「精细调节」→ 勾「开机自动启动」→ 会写一条计划任务
   （登录即启动、静默提权、缩到托盘，并重放上次生效的方案）

细节见 README.md，或 https://github.com/xiao-shan-6/EcoCore
"@ | Set-Content (Join-Path $stage "使用说明.txt") -Encoding UTF8

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force

Remove-Item $buildDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ("  {0,10:N0} B  {1}  ({2} MB)" -f (Get-Item $zip).Length, (Split-Path $zip -Leaf), [math]::Round((Get-Item $zip).Length / 1MB, 2))
Write-Host "  SHA256： $((Get-FileHash $zip -Algorithm SHA256).Hash)"
Write-Host ""
Write-Host "提示：清单声明了 requireAdministrator，双击会弹 UAC；允许后才是管理员权限。"
Write-Host "      进程绑核 / 效率模式不要管理员也能生效；频率上限 / 睿频 / EPP / 电源滑块必须提权。"
