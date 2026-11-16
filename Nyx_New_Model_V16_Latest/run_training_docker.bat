@echo off
setlocal
cd /d "%~dp0"

echo ==========================================
echo NYX V16 - STANDALONE DOCKER TRAINING
 echo ==========================================
echo.

where docker >nul 2>nul
if errorlevel 1 goto no_docker

docker build -t nyx-v16 .
if errorlevel 1 goto failed

docker run --rm -v "%cd%\model_bundle_v16:/app/model_bundle_v16" nyx-v16
if errorlevel 1 goto failed

echo.
echo V16 Docker training completed.
pause
exit /b 0

:no_docker
echo Docker Desktop was not found on this computer.
echo Use setup_python314.bat first. Docker is only the standalone fallback
 echo when Windows Application Control blocks scikit-learn .pyd files.
pause
exit /b 1

:failed
echo.
echo Docker training failed. See the error above.
pause
exit /b 1
