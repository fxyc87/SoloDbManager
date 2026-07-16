@echo off
REM ============================================================
REM  SoloDB Manager — Windows single-file build script
REM
REM  Produces: dist-win\SoloDbManager.exe  (single self-contained file,
REM  no companion DLLs, no .NET runtime needed on target machine)
REM
REM  Prerequisites: .NET 10 SDK (https://dot.net)
REM  Run from project root: scripts\build-windows.bat
REM ============================================================
setlocal
cd /d "%~dp0\.."

echo Cleaning previous build...
if exist dist-win rmdir /s /q dist-win

echo Building SoloDbManager (WebView2 single-file)...
dotnet publish solodb-csharp\SoloDbManager.csproj -c Release -r win-x64 -o dist-win
if errorlevel 1 (
  echo BUILD FAILED. Make sure .NET 10 SDK is installed.
  exit /b 1
)

del /q dist-win\*.xml 2>nul
del /q dist-win\*.pdb 2>nul

echo.
echo ============================================
echo  Build complete!
echo ============================================
dir /b dist-win
echo.
echo  Output: dist-win\SoloDbManager.exe
echo  Run:    dist-win\SoloDbManager.exe
echo ============================================
endlocal
