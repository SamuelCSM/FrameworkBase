@echo off
chcp 65001 >nul
pushd "%~dp0"

echo [CI] 本地质量门禁：编译 + EditMode 测试（须先关闭 Unity 编辑器）...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\ci\run-ci.ps1"
if errorlevel 1 goto err

echo.
echo [CI] 通过。
popd
pause
exit /b 0

:err
echo.
echo [CI] 未通过。请查看上方输出与 Logs\ci\ 下的日志。
popd
pause
exit /b 1
