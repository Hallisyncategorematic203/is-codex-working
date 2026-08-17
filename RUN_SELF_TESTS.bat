@echo off
setlocal EnableExtensions
cd /d "%~dp0"
call BUILD.bat --with-tests
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
set "TEST_BUILD_DIR="
set /p TEST_BUILD_DIR=<"bin\CURRENT_TEST_BUILD.txt"
if not defined TEST_BUILD_DIR (
  echo [ERROR] Test build pointer is empty.
  pause
  exit /b 1
)
findstr /r /x /c:"bin\\test-build-[0-9][0-9]*-[0-9][0-9]*" "bin\CURRENT_TEST_BUILD.txt" >nul
if errorlevel 1 (
  echo [ERROR] Test build pointer is outside the isolated test-build namespace.
  pause
  exit /b 1
)
find /v /c "" <"bin\CURRENT_TEST_BUILD.txt" | findstr /r /x "1" >nul
if errorlevel 1 (
  echo [ERROR] Test build pointer must contain exactly one line.
  pause
  exit /b 1
)
set "TEST_BUILD_OK="
set /p TEST_BUILD_OK=<"bin\TEST_BUILD_OK.txt"
findstr /r /x /c:"bin\\test-build-[0-9][0-9]*-[0-9][0-9]*" "bin\TEST_BUILD_OK.txt" >nul
if errorlevel 1 (
  echo [ERROR] Test build success receipt is invalid.
  pause
  exit /b 1
)
find /v /c "" <"bin\TEST_BUILD_OK.txt" | findstr /r /x "1" >nul
if errorlevel 1 (
  echo [ERROR] Test build success receipt must contain exactly one line.
  pause
  exit /b 1
)
if /i not "%TEST_BUILD_OK%"=="%TEST_BUILD_DIR%" (
  echo [ERROR] Test build has no matching successful build receipt.
  pause
  exit /b 1
)
if not exist "%BUILD_DIR%\IsCodexWorking.exe" (
  echo [ERROR] Production build pointer is missing or invalid.
  pause
  exit /b 1
)
if not exist "%TEST_BUILD_DIR%\IsCodexWorking.Tests.exe" (
  echo [ERROR] Test build pointer is missing or invalid.
  pause
  exit /b 1
)
"%TEST_BUILD_DIR%\IsCodexWorking.Tests.exe" --self-test
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" (
  if exist "%BUILD_DIR%" rmdir /s /q "%BUILD_DIR%"
  del /q "bin\CURRENT_BUILD.txt" 2>nul
  del /q "bin\BUILD_OK.txt" 2>nul
)
echo.
if "%CODE%"=="0" (echo All self-tests passed.) else (echo Self-tests failed. Failed binaries were removed.)
pause
exit /b %CODE%
