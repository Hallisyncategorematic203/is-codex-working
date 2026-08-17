@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "BUILD_WITH_TESTS=0"
if /i "%~1"=="--with-tests" set "BUILD_WITH_TESTS=1"
if not "%~1"=="" if /i not "%~1"=="--with-tests" (
  echo [ERROR] Unknown build option: %~1
  exit /b 1
)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist bin mkdir bin >nul 2>&1
if not exist bin (
  echo [ERROR] Could not create the bin directory.
  exit /b 1
)
if exist "bin\BUILD_LOCK" (
  echo [ERROR] Another build is already running.
  exit /b 1
)
mkdir "bin\BUILD_LOCK" >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Could not acquire the build lock.
  exit /b 1
)
call :invalidate_current_build
if errorlevel 1 goto build_failed
call :invalidate_test_build
if errorlevel 1 goto build_failed

if not exist "%CSC%" (
  echo [ERROR] Windows .NET Framework compiler not found.
  echo This preview needs the built-in .NET Framework 4.x compiler.
  goto build_failed
)

set "OUTDIR=bin\build-%RANDOM%-%RANDOM%"
if exist "%OUTDIR%" goto choose_output_again
mkdir "%OUTDIR%" >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Could not create isolated build output.
  goto build_failed
)
set "TEST_OUTDIR="
if "%BUILD_WITH_TESTS%"=="1" goto choose_test_output
goto compile

:choose_output_again
set "OUTDIR=bin\build-%RANDOM%-%RANDOM%"
if exist "%OUTDIR%" goto choose_output_again
mkdir "%OUTDIR%" >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Could not create isolated build output.
  goto build_failed
)
set "TEST_OUTDIR="
if "%BUILD_WITH_TESTS%"=="1" goto choose_test_output
goto compile

:choose_test_output
set "TEST_OUTDIR=bin\test-build-%RANDOM%-%RANDOM%"
if exist "%TEST_OUTDIR%" goto choose_test_output
mkdir "%TEST_OUTDIR%" >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Could not create isolated test output.
  goto build_failed
)
goto compile

:compile
if not exist "src\Models.cs" (
  echo [ERROR] Required source files are missing from this package.
  if exist "src\src\Models.cs" echo [ERROR] Invalid package layout detected: source files are under src\src.
  echo [ERROR] Expected BUILD.bat and src\Models.cs to be next to each other.
  goto build_failed
)
set "PRODUCTION_SOURCES=src\Models.cs src\JsonUtil.cs src\ProcessProbe.cs src\Monitor.cs src\Ui.cs src\Program.cs"
set "TEST_SOURCES=%PRODUCTION_SOURCES% src\TestProgram.cs src\SelfTests.cs src\RequiredRegressionTests.cs src\StressTests.cs"
if "%BUILD_WITH_TESTS%"=="1" (
  for %%F in (src\TestProgram.cs src\SelfTests.cs src\RequiredRegressionTests.cs src\StressTests.cs) do (
    if not exist "%%F" (
      echo [ERROR] Test source file is missing for --with-tests: %%F
      goto build_failed
    )
  )
)
echo Building Is Codex Working?...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:"app.manifest" /out:"%OUTDIR%\IsCodexWorking.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  %PRODUCTION_SOURCES%
if errorlevel 1 goto build_failed

if "%BUILD_WITH_TESTS%"=="1" (
  "%CSC%" /nologo /target:exe /define:TEST_BUILD /main:IsCodexWorking.TestProgram /optimize+ /platform:anycpu /out:"%TEST_OUTDIR%\IsCodexWorking.Tests.exe" ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
    %TEST_SOURCES%
  if errorlevel 1 goto build_failed
)

>"bin\CURRENT_BUILD.txt.tmp" echo %OUTDIR%
if not exist "bin\CURRENT_BUILD.txt.tmp" goto build_failed
move /y "bin\CURRENT_BUILD.txt.tmp" "bin\CURRENT_BUILD.txt" >nul
if errorlevel 1 goto build_failed
if not exist "bin\CURRENT_BUILD.txt" goto build_failed
>"bin\BUILD_OK.txt.tmp" echo %OUTDIR%
if not exist "bin\BUILD_OK.txt.tmp" goto build_failed
move /y "bin\BUILD_OK.txt.tmp" "bin\BUILD_OK.txt" >nul
if errorlevel 1 goto build_failed
if not exist "bin\BUILD_OK.txt" goto build_failed
if "%BUILD_WITH_TESTS%"=="1" (
  >"bin\CURRENT_TEST_BUILD.txt.tmp" echo %TEST_OUTDIR%
  if not exist "bin\CURRENT_TEST_BUILD.txt.tmp" goto build_failed
  move /y "bin\CURRENT_TEST_BUILD.txt.tmp" "bin\CURRENT_TEST_BUILD.txt" >nul
  if errorlevel 1 goto build_failed
  if not exist "bin\CURRENT_TEST_BUILD.txt" goto build_failed
  >"bin\TEST_BUILD_OK.txt.tmp" echo %TEST_OUTDIR%
  if not exist "bin\TEST_BUILD_OK.txt.tmp" goto build_failed
  move /y "bin\TEST_BUILD_OK.txt.tmp" "bin\TEST_BUILD_OK.txt" >nul
  if errorlevel 1 goto build_failed
  if not exist "bin\TEST_BUILD_OK.txt" goto build_failed
)
echo Build complete: %OUTDIR%\IsCodexWorking.exe
call :release_build_lock
if errorlevel 1 (
  echo [ERROR] Could not release the build lock.
  exit /b 1
)
exit /b 0

:build_failed
if exist "%OUTDIR%" rmdir /s /q "%OUTDIR%"
if exist "%TEST_OUTDIR%" rmdir /s /q "%TEST_OUTDIR%"
del /q "bin\CURRENT_BUILD.txt.tmp" 2>nul
del /q "bin\CURRENT_TEST_BUILD.txt.tmp" 2>nul
call :invalidate_current_build
if errorlevel 1 echo [ERROR] Current build pointer could not be invalidated.
call :invalidate_test_build
if errorlevel 1 echo [ERROR] Test build pointer could not be invalidated.
echo [ERROR] Build failed. No executable was launched.
call :release_build_lock
exit /b 1

:release_build_lock
if exist "bin\BUILD_LOCK" rmdir /s /q "bin\BUILD_LOCK" >nul 2>&1
if exist "bin\BUILD_LOCK" exit /b 1
exit /b 0

:invalidate_current_build
if exist "bin\CURRENT_BUILD.txt.tmp" del /q "bin\CURRENT_BUILD.txt.tmp" >nul 2>&1
if exist "bin\CURRENT_BUILD.txt.tmp" (
  echo [ERROR] Could not remove the temporary build pointer.
  exit /b 1
)
if exist "bin\CURRENT_BUILD.txt" del /q "bin\CURRENT_BUILD.txt" >nul 2>&1
if exist "bin\CURRENT_BUILD.txt" (
  echo [ERROR] Could not invalidate the current build pointer.
  exit /b 1
)
if exist "bin\BUILD_OK.txt.tmp" del /q "bin\BUILD_OK.txt.tmp" >nul 2>&1
if exist "bin\BUILD_OK.txt.tmp" (
  echo [ERROR] Could not remove the build success receipt.
  exit /b 1
)
if exist "bin\BUILD_OK.txt" del /q "bin\BUILD_OK.txt" >nul 2>&1
if exist "bin\BUILD_OK.txt" (
  echo [ERROR] Could not invalidate the build success receipt.
  exit /b 1
)
exit /b 0

:invalidate_test_build
if exist "bin\CURRENT_TEST_BUILD.txt.tmp" del /q "bin\CURRENT_TEST_BUILD.txt.tmp" >nul 2>&1
if exist "bin\CURRENT_TEST_BUILD.txt.tmp" (
  echo [ERROR] Could not remove the temporary test build pointer.
  exit /b 1
)
if exist "bin\CURRENT_TEST_BUILD.txt" del /q "bin\CURRENT_TEST_BUILD.txt" >nul 2>&1
if exist "bin\CURRENT_TEST_BUILD.txt" (
  echo [ERROR] Could not invalidate the current test build pointer.
  exit /b 1
)
if exist "bin\TEST_BUILD_OK.txt.tmp" del /q "bin\TEST_BUILD_OK.txt.tmp" >nul 2>&1
if exist "bin\TEST_BUILD_OK.txt.tmp" (
  echo [ERROR] Could not remove the temporary test build receipt.
  exit /b 1
)
if exist "bin\TEST_BUILD_OK.txt" del /q "bin\TEST_BUILD_OK.txt" >nul 2>&1
if exist "bin\TEST_BUILD_OK.txt" (
  echo [ERROR] Could not invalidate the test build receipt.
  exit /b 1
)
exit /b 0
