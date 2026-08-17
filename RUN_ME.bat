@echo off
setlocal EnableExtensions
cd /d "%~dp0"

call BUILD.bat
if errorlevel 1 (
  echo.
  pause
  exit /b 1
)

set "BUILD_DIR="
set /p BUILD_DIR=<"bin\CURRENT_BUILD.txt"
if not defined BUILD_DIR (
  echo [ERROR] Current build pointer is empty.
  pause
  exit /b 1
)
findstr /r /x /c:"bin\\build-[0-9][0-9]*-[0-9][0-9]*" "bin\CURRENT_BUILD.txt" >nul
if errorlevel 1 (
  echo [ERROR] Current build pointer is outside the isolated build namespace.
  pause
  exit /b 1
)
find /v /c "" <"bin\CURRENT_BUILD.txt" | findstr /r /x "1" >nul
if errorlevel 1 (
  echo [ERROR] Current build pointer must contain exactly one line.
  pause
  exit /b 1
)
set "BUILD_OK="
set /p BUILD_OK=<"bin\BUILD_OK.txt"
findstr /r /x /c:"bin\\build-[0-9][0-9]*-[0-9][0-9]*" "bin\BUILD_OK.txt" >nul
if errorlevel 1 (
  echo [ERROR] Current build success receipt is invalid.
  pause
  exit /b 1
)
find /v /c "" <"bin\BUILD_OK.txt" | findstr /r /x "1" >nul
if errorlevel 1 (
  echo [ERROR] Current build success receipt must contain exactly one line.
  pause
  exit /b 1
)
if /i not "%BUILD_OK%"=="%BUILD_DIR%" (
  echo [ERROR] Current build has no matching successful build receipt.
  pause
  exit /b 1
)
if not exist "%BUILD_DIR%\IsCodexWorking.exe" (
  echo [ERROR] Current build application is missing.
  pause
  exit /b 1
)
start "" "%BUILD_DIR%\IsCodexWorking.exe"
exit /b 0
