@echo off
setlocal
cd /d "%~dp0"

echo ==========================================
echo NYX INSIGHTS V16 - LOCAL TEST
 echo ==========================================
echo.

if not exist ".venv\Scripts\python.exe" goto no_environment

.venv\Scripts\python.exe -c "import numpy, pandas, scipy, joblib, sklearn" >nul 2>&1
if errorlevel 1 goto blocked

.venv\Scripts\python.exe test_local_v16.py
if errorlevel 1 goto failed
pause
exit /b 0

:no_environment
echo V16 .venv was not found. Run setup_python314.bat first.
pause
exit /b 1

:blocked
echo Windows Application Control is blocking the V16 .venv.
echo Run setup_python314.bat again.
pause
exit /b 1

:failed
echo Local test failed. See the error above.
pause
exit /b 1
