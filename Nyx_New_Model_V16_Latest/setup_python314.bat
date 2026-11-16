@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

 echo ==========================================
 echo NYX V16 - STANDALONE PYTHON 3.14 SETUP
 echo ==========================================
 echo.
 echo This setup is fully standalone.
 echo It only creates and uses Nyx_New_Model_V16\.venv.
 echo No older Nyx model folders are required.
 echo.

where py >nul 2>nul
if errorlevel 1 goto no_python

py -3.14 --version
if errorlevel 1 goto no_python

if exist ".venv" (
    echo Removing the previous V16 .venv...
    rmdir /s /q .venv
)

call :make_base_env
if errorlevel 1 goto failed

rem ------------------------------------------------------------
rem Try scikit-learn releases that support Python 3.14.
rem Windows Smart App Control can treat different compiled builds
rem differently, so V16 tests each build inside its OWN .venv.
rem ------------------------------------------------------------
call :try_sklearn 1.8.0
if not errorlevel 1 goto success

call :reset_for_next_sklearn
if errorlevel 1 goto failed
call :try_sklearn 1.9.0
if not errorlevel 1 goto success

call :reset_for_next_sklearn
if errorlevel 1 goto failed
call :try_sklearn 1.9.1
if not errorlevel 1 goto success

goto blocked

:make_base_env
echo Creating V16 .venv...
py -3.14 -m venv .venv
if errorlevel 1 exit /b 1

echo.
echo Upgrading pip...
.venv\Scripts\python.exe -m pip install --upgrade pip
if errorlevel 1 exit /b 1

echo.
echo Installing V16 base packages...
.venv\Scripts\python.exe -m pip install --no-cache-dir --only-binary=:all: numpy pandas==3.0.6 scipy joblib threadpoolctl
if errorlevel 1 exit /b 1

echo.
echo Testing base imports...
.venv\Scripts\python.exe -c "import numpy, pandas, scipy, joblib; print('Base scientific packages OK'); print('pandas', pandas.__version__)"
if errorlevel 1 exit /b 1
exit /b 0

:reset_for_next_sklearn
echo.
echo Rebuilding V16 .venv for another compatible scikit-learn build...
rmdir /s /q .venv
call :make_base_env
exit /b %errorlevel%

:try_sklearn
set SKVER=%1
echo.
echo ==========================================
echo Trying scikit-learn %SKVER%
echo ==========================================
.venv\Scripts\python.exe -m pip install --no-cache-dir --only-binary=:all: scikit-learn==%SKVER%
if errorlevel 1 exit /b 1

echo Testing the exact ML modules V16 uses...
.venv\Scripts\python.exe -c "import sklearn; from sklearn.ensemble import ExtraTreesRegressor, RandomForestRegressor, GradientBoostingRegressor, HistGradientBoostingRegressor; from sklearn.model_selection import GroupKFold; from sklearn.feature_selection import SelectKBest, VarianceThreshold, f_regression; from sklearn.metrics import r2_score, mean_absolute_error; print('V16 scikit-learn imports OK:', sklearn.__version__)"
if errorlevel 1 (
    echo scikit-learn %SKVER% was installed but Windows blocked a compiled module.
    exit /b 1
)

> .venv\V16_SKLEARN_VERSION.txt echo %SKVER%
exit /b 0

:success
echo.
echo ==========================================
echo V16 STANDALONE SETUP COMPLETE
 echo ==========================================
 echo Environment: Nyx_New_Model_V16\.venv
 if exist ".venv\V16_SKLEARN_VERSION.txt" (
     set /p WORKING_SK=<.venv\V16_SKLEARN_VERSION.txt
     echo Working scikit-learn: !WORKING_SK!
 )
 echo.
 echo Next command:
 echo     run_training.bat
 echo ==========================================
pause
exit /b 0

:blocked
echo.
echo ==========================================
echo WINDOWS APPLICATION CONTROL BLOCKED ALL TESTED BUILDS
 echo ==========================================
 echo V16 is standalone, but Windows is refusing to load scikit-learn's
 echo compiled .pyd files. A project batch file cannot safely override an
 echo enforced Windows Application Control / Smart App Control policy.
 echo.
 echo The V16 model code itself was not reached.
 echo Check Windows Security ^> App ^& browser control ^> Smart App Control,
 echo or use the included Docker option if Docker Desktop is available.
 echo ==========================================
pause
exit /b 1

:no_python
echo.
echo Python 3.14 or the Windows py launcher was not found.
pause
exit /b 1

:failed
echo.
echo ==========================================
echo V16 STANDALONE ENVIRONMENT SETUP FAILED
 echo ==========================================
pause
exit /b 1
