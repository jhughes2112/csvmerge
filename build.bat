@echo off
setlocal
cd /d "%~dp0"

echo Building csvmerge as a single native executable (dist\csvmerge.exe)...
echo.

rem Native AOT: small, instant startup, no .NET runtime needed. The AOT
rem toolchain locates MSVC via vswhere.exe, which lives in the VS Installer
rem directory but isn't usually on PATH.
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
    set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
)
dotnet publish src\CsvMerge -c Release -r win-x64 -o dist /p:PublishAot=true /p:PackAsTool=false
if errorlevel 1 (
    echo.
    echo Native AOT publish failed.
    echo.
    echo Building csvmerge requires the native AOT toolchain:
    echo   - .NET 10 SDK or newer       https://dotnet.microsoft.com/download
    echo   - Visual Studio 2022 with the "Desktop development with C++" workload
    echo     ^(provides the MSVC linker the AOT compiler needs^), or install just
    echo     the build tools: https://visualstudio.microsoft.com/visual-cpp-build-tools/
    echo.
    echo Install the missing piece and re-run build.bat.
    exit /b 1
)

dist\csvmerge.exe --help >nul 2>&1
if errorlevel 1 (
    echo Smoke test failed: dist\csvmerge.exe --help returned an error.
    exit /b 1
)

echo.
echo Built: %~dp0dist\csvmerge.exe
echo Put it on PATH, then run "csvmerge install" in your repos.
