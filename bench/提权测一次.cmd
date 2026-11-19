@echo off
chcp 936 >nul
cd /d "%~dp0.."
set EXE=src\EcoCore\bin\x64\Release\net10.0-windows\EcoCore.exe
set OUT=%~dp0admin-maxsaving

echo 正在请求管理员权限（UAC 弹窗请点「是」）…
powershell -NoProfile -Command "Start-Process -FilePath '%EXE%' -ArgumentList @('--bench','--ab','--profile','maxsaving','--rounds','3','--seconds','75','--settle','20','--load','2','--out','%OUT%') -Verb RunAs"

echo.
echo 已启动。它会跑约 10 分钟，窗口不显示内容属正常（结果写文件）。
echo 跑完后看：%OUT%\summary.txt
echo.
pause
