@echo off
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" (
  echo .NET 10 Runtime not found. Install it with: winget install Microsoft.DotNet.Runtime.10
  exit /b 1
)
"%DOTNET%" "%~dp0WfSysMon.dll" %*
