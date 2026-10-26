@echo off
rem Builds and opens the 16:9 editor (tools/WideEditor). No console stays open.
cd /d "%~dp0"
dotnet build tools\WideEditor -c Release -v q -nologo
if errorlevel 1 (
    pause
    exit /b 1
)
start "" "%~dp0tools\WideEditor\bin\Release\net10.0\WideEditor.exe"
