@echo off
setlocal EnableExtensions

rem ============================================================================
rem  Subtitle Studio - build_and_launch.bat
rem  Restores, publishes the single-file self-contained x64 EXE to .\publish
rem  and offers to launch it. The EXE is found by name (glob); the version is
rem  NOT parsed from the csproj here on purpose.
rem ============================================================================

cd /d "%~dp0"
set "PROJ=src\SubtitleStudio\SubtitleStudio.csproj"
set "OUT=%~dp0publish"

where dotnet >nul 2>nul
if errorlevel 1 goto :nodotnet

echo.
echo [1/3] Restoring packages...
dotnet restore "%PROJ%" -r win-x64
if errorlevel 1 goto :fail

echo.
echo [2/3] Publishing single-file EXE...
call :closerunning
if exist "%OUT%" rmdir /s /q "%OUT%" 2>nul
if exist "%OUT%" goto :locked
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%OUT%"
if errorlevel 1 goto :fail

echo.
echo [3/3] Locating EXE...
set "EXE="
for %%F in ("%OUT%\SubtitleStudio*.exe") do set "EXE=%%~fF"
if not defined EXE goto :noexe

echo Built: "%EXE%"
echo.
choice /c YN /m "Launch Subtitle Studio now"
if errorlevel 2 goto :done
start "" "%EXE%"
goto :done

:locked
echo [ERROR] The publish folder is locked, usually by a running Subtitle Studio. Close it and run this again.
goto :fail

:nodotnet
echo [ERROR] The .NET SDK was not found on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
goto :fail

:noexe
echo [ERROR] Publish reported success but no SubtitleStudio*.exe was found in the publish folder.
goto :fail

:fail
echo.
echo BUILD FAILED.
pause
endlocal
exit /b 1

:done
endlocal
exit /b 0

rem ----------------------------------------------------------------------------
rem  A running Subtitle Studio (any version) locks its EXE in the publish folder.
rem  Offer to close it; answering N leaves it running (and the build will stop).
rem ----------------------------------------------------------------------------
:closerunning
tasklist /fi "imagename eq SubtitleStudio*" 2>nul | find /i "SubtitleStudio" >nul
if errorlevel 1 exit /b 0
echo Subtitle Studio is running and locks the files being replaced.
echo Unsaved subtitle changes in it would be lost.
choice /c YN /m "Close it now"
if errorlevel 2 exit /b 0
taskkill /f /im "SubtitleStudio*" >nul 2>nul
timeout /t 2 /nobreak >nul
exit /b 0
