@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  build.cmd - compiles the UUP Dump Fetcher sources into uupdump.exe
REM
REM  Sources (all in this folder):
REM    Core.cs Theme.cs Dialogs.cs Net.cs WuClient.cs WimLibApi.cs CabinetApi.cs
REM    Fetcher.cs Processor.cs MainForm.cs
REM
REM  Required files in assets\:
REM    app.ico
REM    libwim-15.dll
REM
REM  wimlib-imagex.exe is NOT needed - WimLibApi.cs P/Invokes libwim-15.dll
REM  directly (the exe was only ever a thin CLI wrapper over that library),
REM  and handles WIM extraction too (both the real Windows install.wim and
REM  modern WIM-formatted ".msu" checkpoint packages), not just capture.
REM
REM  7z.exe/7z.dll are NOT needed either (removed - see Processor.cs's
REM  ToolHelper comment): the classic-CAB fallback for non-WIM .msu packages
REM  now goes through CabinetApi.cs, a direct P/Invoke binding to the OS's own
REM  cabinet.dll - no embedded binary at all for that path.
REM
REM  libwim-15.dll is embedded as a managed resource and extracted to
REM  %TEMP%\uupdump_tools_<version>\ at runtime, so uupdump.exe ships alone.
REM
REM  version.txt holds the assembly version and is bumped (revision number
REM  only) on every successful compile - see the version block below.
REM  AssemblyVersion.generated.cs is regenerated from it each time; it is a
REM  build output, not a real source file, but must still be listed in
REM  %SOURCES% so csc.exe picks it up.
REM ============================================================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set VERFILE=version.txt
set VERSRC=AssemblyVersion.generated.cs
set SOURCES=Core.cs Theme.cs Dialogs.cs Net.cs WuClient.cs WimLibApi.cs CabinetApi.cs Fetcher.cs Processor.cs MainForm.cs %VERSRC%
set ASSETS=assets

if not exist "%VERFILE%" (
    echo 2.0.0.0>"%VERFILE%"
) else (
    set /p CURVER=<"%VERFILE%"
    for /f "tokens=1-4 delims=." %%a in ("!CURVER!") do (
        set VMAJOR=%%a
        set VMINOR=%%b
        set VBUILD=%%c
        set VREV=%%d
    )
    set /a VREV=!VREV!+1
    >"%VERFILE%" echo !VMAJOR!.!VMINOR!.!VBUILD!.!VREV!
)
set /p APPVERSION=<"%VERFILE%"

(
    echo using System.Reflection;
    echo [assembly: AssemblyVersion^("%APPVERSION%"^)]
    echo [assembly: AssemblyFileVersion^("%APPVERSION%"^)]
)>"%VERSRC%"

if not exist "%CSC%" (
    echo ERROR: csc.exe not found at:
    echo   %CSC%
    echo Install the .NET Framework 4.x developer pack.
    exit /b 1
)

for %%F in (%SOURCES%) do (
    if not exist "%%F" (
        echo ERROR: missing required source file: %%F
        exit /b 1
    )
)

for %%F in (app.ico libwim-15.dll) do (
    if not exist "%ASSETS%\%%F" (
        echo ERROR: missing required file: %ASSETS%\%%F
        exit /b 1
    )
)

echo Compiling uupdump.exe v%APPVERSION% ...

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:%ASSETS%\app.ico /resource:%ASSETS%\libwim-15.dll,tools.libwim-15.dll /out:uupdump.exe %SOURCES%

if errorlevel 1 (
    echo.
    echo BUILD FAILED.
    exit /b 1
)

echo.
echo BUILD OK -^> uupdump.exe v%APPVERSION%
endlocal
