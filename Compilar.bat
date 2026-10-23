@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo No se encontro .NET SDK. Instala el SDK de .NET 8 para Windows y vuelve a ejecutar este archivo.
  pause
  exit /b 1
)
dotnet publish MediaPlayerResumeAssistant.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
if errorlevel 1 (
  echo.
  echo La compilacion fallo. Revisa el mensaje anterior y comprueba que tienes instalado el SDK de .NET 8 con soporte de escritorio.
  pause
  exit /b 1
)
echo.
echo Aplicacion compilada en: bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\MediaPlayerResumeAssistant.exe
pause
