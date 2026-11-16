@echo off
setlocal
cd /d "%~dp0"

echo ==========================================
echo NYX INSIGHTS - MODEL VERSION 15 TRAINING
echo ==========================================
echo.

if not exist ".venv\Scripts\python.exe" goto no_environment

echo Checking V16 standalone environment...
.venv\Scripts\python.exe -c "import numpy, pandas, scipy, joblib, sklearn; from sklearn.ensemble import ExtraTreesRegressor, RandomForestRegressor, GradientBoostingRegressor, HistGradientBoostingRegressor" >nul 2>&1
if errorlevel 1 goto blocked

echo Starting V16 training...
echo.
.venv\Scripts\python.exe train_nyx_model_v16.py
if errorlevel 1 goto failed

goto success

:no_environment
echo V16 .venv was not found.
echo Run setup_python314.bat first.
pause
exit /b 1

:blocked
echo.
echo ==========================================
echo V16 ENVIRONMENT IS BLOCKED BY WINDOWS
 echo ==========================================
echo Run setup_python314.bat again. It will test multiple compatible
 echo scikit-learn builds inside V16's own .venv.
echo No older Nyx model folder is used.
pause
exit /b 1

:failed
echo.
echo ==========================================
echo V16 TRAINING FAILED - SEE ERROR ABOVE
 echo ==========================================
pause
exit /b 1

:success
echo.
echo ==========================================
echo V16 TRAINING COMPLETED SUCCESSFULLY
 echo ==========================================
echo Model files are in model_bundle_v16
echo ==========================================
pause
exit /b 0
