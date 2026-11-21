@echo off
rem 解除 SmartScreen「已保护你的电脑」提示：去掉文件的「来自 Internet」标记
setlocal
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=%~dp0EcoCore.exe"
if not exist "%TARGET%" (
  echo [ERROR] 找不到文件：%TARGET%
  echo 用法：把本脚本放到 EcoCore.exe 所在文件夹，双击运行。
  pause
  exit /b 1
)
powershell -NoProfile -Command "Unblock-File -LiteralPath '%TARGET%'; Get-Item -LiteralPath '%TARGET%' | Select-Object -ExpandProperty Name"
if errorlevel 1 (
  echo [WARN] 解除失败，试试右键 EcoCore.exe - 属性 - 勾选「解除锁定」- 确定。
) else (
  echo.
  echo 已解除 Internet 标记，现在双击 EcoCore.exe 不会再被 SmartScreen 拦截。
)
pause
