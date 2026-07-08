@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
set "CI_EXIT_CODE=0"

echo [CI] FrameworkBase local gate: compile + EditMode + PlayMode tests.
echo [CI] Please close the Unity Editor before running this script.
echo.

if not exist "%~dp0Tools\ci\run-ci.ps1" (
    echo [CI] Missing script: "%~dp0Tools\ci\run-ci.ps1"
    set "CI_EXIT_CODE=1"
    goto err
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\ci\run-ci.ps1" 2>&1
if errorlevel 1 (
    set "CI_EXIT_CODE=%ERRORLEVEL%"
    goto err
)

echo.
echo [CI] Passed.
goto done

:err
echo.
echo [CI] Failed. Check the output above and logs under Logs\ci\.

:done
echo.
pause
exit /b %CI_EXIT_CODE%
