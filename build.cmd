@echo off
rem Builds Bubbels.exe and Bubbels-setup.exe with the C# compiler that ships with
rem the .NET Framework on every Windows machine. No SDK, no packages.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

set ROOT=%~dp0
if not exist "%ROOT%bin" mkdir "%ROOT%bin"

if not exist "%ROOT%build\bubbels.ico" (
  echo [icon] build\bubbels.ico
  "%CSC%" /nologo /out:"%ROOT%bin\MakeIcon.exe" /reference:System.Drawing.dll "%ROOT%tools\MakeIcon.cs" || goto fail
  "%ROOT%bin\MakeIcon.exe" "%ROOT%build\bubbels.ico" || goto fail
)

echo [1/2] Bubbels.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /nowarn:1690 ^
  /win32icon:"%ROOT%build\bubbels.ico" ^
  /out:"%ROOT%bin\Bubbels.exe" ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  "%ROOT%src\*.cs"
if errorlevel 1 goto fail

echo [2/2] Bubbels-setup.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /win32icon:"%ROOT%build\bubbels.ico" ^
  /resource:"%ROOT%bin\Bubbels.exe",Bubbels.exe ^
  /resource:"%ROOT%build\bubbels.ico",bubbels.ico ^
  /out:"%ROOT%bin\Bubbels-setup.exe" ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  "%ROOT%setup\*.cs" "%ROOT%src\Autostart.cs" "%ROOT%src\Strings.cs" "%ROOT%src\Modern.cs"
if errorlevel 1 goto fail

echo.
echo Done:
echo   %ROOT%bin\Bubbels.exe
echo   %ROOT%bin\Bubbels-setup.exe
exit /b 0

:fail
echo.
echo Build failed.
exit /b 1
