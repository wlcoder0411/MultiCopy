@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================================
echo   MultiCopy Build (dotnet publish + Inno Setup)
echo ============================================================
echo.

REM Stop running MultiCopy (avoid exe lock)
taskkill /IM MultiCopy.exe /F >nul 2>&1

REM Read version from csproj (tokens=3 to get value between ^<Version^> and ^</Version^>)
set VERSION=3.2
for /f "tokens=3 delims=<>" %%a in ('findstr "<Version>" src\MultiCopy\MultiCopy.csproj') do set VERSION=%%a
echo Version: %VERSION%
echo.

REM ---------- Step 1/2: dotnet publish ----------
echo ========== Step 1/2 : dotnet publish ==========
echo.
dotnet publish src\MultiCopy\MultiCopy.csproj -p:PublishProfile=win-x64-selfcontained
if errorlevel 1 (
    echo.
    echo [FAIL] dotnet publish failed
    goto :end
)
if not exist "publish\win-x64\MultiCopy.exe" (
    echo [FAIL] Output not found: publish\win-x64\MultiCopy.exe
    goto :end
)
echo.
echo [OK] publish done
echo.

REM ---------- Step 2/2: ISCC compile installer ----------
echo ========== Step 2/2 : Inno Setup compile ==========
echo.

REM Find ISCC.exe: registry first, then PATH, then common install paths
set ISCC=

REM 1. Registry: HKLM WOW6432Node (32-bit install on 64-bit OS)
for /f "tokens=2,*" %%a in ('reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" /v InstallLocation 2^>nul') do (
    if exist "%%bISCC.exe" set ISCC=%%bISCC.exe
)
REM 2. Registry: HKLM (64-bit install)
if not defined ISCC (
    for /f "tokens=2,*" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" /v InstallLocation 2^>nul') do (
        if exist "%%bISCC.exe" set ISCC=%%bISCC.exe
    )
)
REM 3. Registry: HKCU
if not defined ISCC (
    for /f "tokens=2,*" %%a in ('reg query "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" /v InstallLocation 2^>nul') do (
        if exist "%%bISCC.exe" set ISCC=%%bISCC.exe
    )
)

REM 4. PATH
if not defined ISCC (
    where ISCC.exe >nul 2>&1 && set ISCC=ISCC.exe
)

REM 5. Common install paths (including no-space variants like ProgramFiles)
if not defined ISCC (
    for %%p in (
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        "C:\Program Files\Inno Setup 6\ISCC.exe"
        "C:\ProgramFiles\Inno Setup 6\ISCC.exe"
        "D:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        "D:\Program Files\Inno Setup 6\ISCC.exe"
        "D:\ProgramFiles\Inno Setup 6\ISCC.exe"
        "E:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        "E:\Program Files\Inno Setup 6\ISCC.exe"
        "E:\ProgramFiles\Inno Setup 6\ISCC.exe"
        "F:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        "F:\Program Files\Inno Setup 6\ISCC.exe"
        "F:\ProgramFiles\Inno Setup 6\ISCC.exe"
        "G:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        "G:\Program Files\Inno Setup 6\ISCC.exe"
        "G:\ProgramFiles\Inno Setup 6\ISCC.exe"
    ) do if exist %%p set ISCC=%%~p
)

if not defined ISCC (
    echo [FAIL] ISCC.exe not found
    echo Please install Inno Setup 6: https://jrsoftware.org/isdl.php
    goto :end
)

echo ISCC: %ISCC%
echo.

REM Pass absolute paths to iss (avoid working dir relative path issues)
set SOURCE_EXE=%~dp0publish\win-x64\MultiCopy.exe
set DIST_DIR=%~dp0dist
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"

"%ISCC%" /Q /DAppVersion="%VERSION%" /DSourceExe="%SOURCE_EXE%" /DDistDir="%DIST_DIR%" installer\MultiCopy.iss
if errorlevel 1 (
    echo.
    echo [FAIL] ISCC compile failed
    goto :end
)

echo.
echo ============================================================
echo   BUILD COMPLETE
echo   Installer: dist\MultiCopySetup-%VERSION%.exe
echo ============================================================

:end
echo.
pause
