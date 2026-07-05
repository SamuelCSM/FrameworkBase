@echo off
chcp 65001 >nul
pushd "%~dp0"

echo [ProtoGen] building tool (first run restores protoc)...
dotnet build "%~dp0Tools\ProtoGen\ProtoGen.csproj" -c Debug -v quiet
if errorlevel 1 goto err

echo [ProtoGen] generating protocol...
dotnet "%~dp0Tools\ProtoGenin\Debug
et8.0\ProtoGen.dll" %*
if errorlevel 1 goto err

echo.
echo [ProtoGen] Done. 协议已生成。
popd
pause
exit /b 0

:err
echo.
echo [ProtoGen] FAILED 生成失败，见上方错误。
popd
pause
exit /b 1
