@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo [ProtoGen] building tool (first run restores protoc)...
dotnet build Tools\ProtoGen\ProtoGen.csproj -c Debug -v quiet
if errorlevel 1 goto :err

echo [ProtoGen] generating protocol...
dotnet Tools\ProtoGenin\Debug
et8.0\ProtoGen.dll %*
if errorlevel 1 goto :err

echo.
echo [ProtoGen] Done. 协议已生成。
pause
exit /b 0

:err
echo.
echo [ProtoGen] FAILED 生成失败，见上方错误。
pause
exit /b 1
