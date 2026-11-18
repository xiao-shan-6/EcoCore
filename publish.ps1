# 一键发布：编译成「单个 exe」（自包含运行时，目标机不用装 .NET）
# 用法：pwsh -File publish.ps1
# 产物：release\EcoCore.exe  —— 双击即用，带 UAC 提权

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# dotnet CLI 的 home 必须在工程内，否则沙箱下写 %USERPROFILE%\.dotnet 会被拒绝
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet"
$env:NUGET_PACKAGES  = Join-Path $root ".nuget"
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

$outDir = Join-Path $root "release"
$proj   = Join-Path $root "src\EcoCore\EcoCore.csproj"

Write-Host "== 发布单文件 exe ==" -ForegroundColor Cyan
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
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$files = Get-ChildItem $outDir -File | Sort-Object Length -Descending
Write-Host ""
Write-Host "产物目录: $outDir"
foreach ($f in $files) {
    Write-Host ("  {0,10:N0} B  {1}" -f $f.Length, $f.Name)
}

$exe = Join-Path $outDir "EcoCore.exe"
if (-not (Test-Path $exe)) { throw "没有找到 EcoCore.exe" }

# 单文件校验：目录里除了 exe / pdb 不该再有多余的运行时文件
$extra = $files | Where-Object { $_.Name -notin @("EcoCore.exe", "EcoCore.pdb") }
if ($extra) {
    Write-Warning ("单文件包里还有别的文件：" + ($extra.Name -join ", "))
} else {
    Write-Host "单文件校验通过：目录里只有 EcoCore.exe" -ForegroundColor Green
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ""
Write-Host "主程序：  $exe"
Write-Host "大小：    $size MB"
Write-Host "SHA256：  $hash"
Write-Host ""
Write-Host "提示：清单声明了 requireAdministrator，双击会弹 UAC；允许后才是管理员权限。"
Write-Host "      进程绑核 / 效率模式不要管理员也能生效；频率上限 / 睿频 / EPP / 电源滑块必须提权。"
