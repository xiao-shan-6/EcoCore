@echo off
cd /d "%~dp0.."
set "EXE=%~dp0..\src\EcoCore\bin\x64\Release\net10.0-windows\EcoCore.exe"
set "OUT=%~dp0admin-maxsaving"

if not exist "%EXE%" (
  echo [ERROR] Not built yet: %EXE%
  echo Run build.ps1 first.
  pause
  exit /b 1
)

echo Requesting administrator rights, click YES on the UAC prompt...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%EXE%' -ArgumentList @('--bench','--ab','--profile','maxsaving','--rounds','3','--seconds','75','--settle','20','--load','2','--out','%OUT%') -Verb RunAs"

echo.
echo Started. It runs about 10 minutes and writes no console output.
echo After it finishes read: %OUT%\summary.txt
echo.
pause
