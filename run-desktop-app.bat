@echo off
setlocal
rem Builds the Windows desktop application and starts it.
rem Double-click this file, or run it from any directory. No administrator rights are needed:
rem the application is unpackaged, so it runs straight from the publish folder.
cd /d "%~dp0"

set "PROJECT=app\XrplFile.App\XrplFile.App.csproj"
set "TFM=net10.0-windows10.0.19041.0"
set "LAYOUT=app\XrplFile.App\bin\publish"
set "EXE=XrplFile.App.exe"

echo ===============================================
echo  XRPL File - build and run
echo ===============================================
echo.

rem --- 1. the .NET SDK -------------------------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: dotnet was not found on PATH. Install the .NET 10 SDK from
  echo        https://dotnet.microsoft.com/download and run this file again.
  goto :fail
)

rem --- 2. the MAUI workload --------------------------------------------------
dotnet workload list 2>nul | findstr /i /c:"maui-windows" /c:"maui " >nul
if errorlevel 1 (
  echo ERROR: the MAUI workload is not installed. From an elevated prompt run
  echo.
  echo            dotnet workload install maui-windows
  echo.
  echo        and run this file again.
  goto :fail
)

rem --- 3. stop a running instance -------------------------------------------
rem A running instance holds its files open, and publishing over them fails.
tasklist /fi "imagename eq %EXE%" 2>nul | find /i "%EXE%" >nul
if not errorlevel 1 (
  echo Closing the running application...
  taskkill /im %EXE% /f >nul 2>&1
  rem ping, not timeout: timeout refuses to run when stdin is redirected.
  ping -n 3 127.0.0.1 >nul
)

rem --- 4. publish ------------------------------------------------------------
echo Building... this takes a few minutes the first time.
dotnet publish "%PROJECT%" -c Release -f %TFM% -o "%LAYOUT%" --nologo -v quiet
if errorlevel 1 (
  echo.
  echo ERROR: the build failed. Scroll up for the first error line.
  goto :fail
)

if not exist "%LAYOUT%\%EXE%" (
  echo ERROR: %LAYOUT%\%EXE% was not produced by the build.
  goto :fail
)
if not exist "%LAYOUT%\wwwroot\index.html" (
  echo ERROR: %LAYOUT% has no wwwroot; the publish layout is incomplete.
  goto :fail
)

rem --- 5. run ----------------------------------------------------------------
echo Starting XRPL File...
start "" "%CD%\%LAYOUT%\%EXE%"
if errorlevel 1 (
  echo ERROR: the application could not be started.
  goto :fail
)

echo.
echo Done. To start it again without rebuilding, run %LAYOUT%\%EXE%.
echo.
pause
exit /b 0

:fail
echo.
pause
exit /b 1
