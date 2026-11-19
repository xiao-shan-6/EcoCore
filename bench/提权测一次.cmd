@echo off
cd /d "%~dp0.."
set "EXE=%~dp0..\src\EcoCore\bin\x64\Release\net10.0-windows\EcoCore.exe"
set "OUT=%~dp0admin-profiles"

if not exist "%EXE%" (
  echo [ERROR] Not built yet: %EXE%
  echo Run build.ps1 first.
  pause
  exit /b 1
)

echo Requesting administrator rights, click YES on the UAC prompt...
echo Comparing: balanced / maxsaving / light  against the untouched baseline.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%EXE%' -ArgumentList @('--bench','--ab','--profiles','balanced,maxsaving,light','--rounds','2','--seconds','75','--settle','20','--load','2','--out','%OUT%') -Verb RunAs"

echo.
echo Started. 2 rounds x (baseline + 3 profiles) = 8 phases, about 13 minutes, no console output.
echo After it finishes read: %OUT%\summary.txt
echo.
pause
