@echo off
setlocal enabledelayedexpansion

echo ════════════════════════════════════════════════════
echo   BG3 Honor Saver - Inno Setup Builder
echo   (Much simpler than WiX!)
echo ════════════════════════════════════════════════════
echo.

:: Check for Inno Setup
if not exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    echo [ERROR] Inno Setup not installed
    echo.
    echo Download from: https://jrsoftware.org/isdl.php
    echo Install to default location
    echo.
    pause
    exit /b 1
)
echo [OK] Inno Setup found
echo.

:: Step 1: Build self-contained application
echo ════════════════════════════════════════════════════
echo Step 1: Building Self-Contained Application
echo ════════════════════════════════════════════════════
echo This includes .NET 10 runtime (~150 MB)
echo Build may take a few minutes...
echo.

dotnet publish -c Release --self-contained true -r win-x64 -p:PublishSingleFile=false

if %errorlevel% neq 0 (
    echo [ERROR] Application build failed
    pause
    exit /b 1
)

echo [OK] Application built
echo.

:: Detect actual output folder
set PUBLISH_DIR=bin\Release\net10.0-windows\win-x64
if exist "bin\Release\net10.0-windows\win-x64\publish" (
    set PUBLISH_DIR=bin\Release\net10.0-windows\win-x64\publish
)

echo Using directory: !PUBLISH_DIR!
echo.

:: Verify exe exists
if not exist "!PUBLISH_DIR!\CBG3BackupManager.exe" (
    echo [ERROR] CBG3BackupManager.exe not found in !PUBLISH_DIR!
    pause
    exit /b 1
)

:: Count files
for /f %%A in ('dir /s /b "!PUBLISH_DIR!" ^| find /c /v ""') do set FILE_COUNT=%%A
echo Found !FILE_COUNT! files
echo.

REM :: Step 2: Build installer with Inno Setup
REM echo ════════════════════════════════════════════════════
REM echo Step 2: Building Installer with Inno Setup
REM echo ════════════════════════════════════════════════════
REM echo.

REM "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" BG3HonorSaver.iss

REM if %errorlevel% neq 0 (
    REM echo [ERROR] Inno Setup build failed
    REM echo.
    REM echo Check BG3HonorSaver.iss for errors
    REM pause
    REM exit /b 1
REM )

REM :: Success!
REM echo.
REM echo ════════════════════════════════════════════════════
REM echo   Build Complete!
REM echo ════════════════════════════════════════════════════
REM echo.
REM echo Output: CBG3HonorSaver-Setup.exe
REM for %%A in (CBG3HonorSaver-Setup.exe) do (
    REM set size=%%~zA
    REM set /a size_mb=!size! / 1048576
    REM echo Size: !size_mb! MB
REM )
REM echo.
REM echo Features:
REM echo   - Desktop shortcut (optional checkbox)
REM echo   - Launch after install (optional checkbox)
REM echo   - .NET 10 check with download prompt
REM echo   - Professional Windows installer
REM echo.
REM echo.
REM pause
