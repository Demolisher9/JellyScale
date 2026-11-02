@echo off
rem Builds JellyScale.exe and JellyScale-Setup.exe on Windows with only built-in tools + NSIS 3.
rem The C# compiler that ships with Windows (.NET Framework 4.x) is enough; the code is C# 5.
setlocal
cd /d "%~dp0\.."
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist build mkdir build

rem res\JellyScale.res (icon, manifest, version info) is prebuilt from res\JellyScale.rc.
rem To rebuild it with the Windows SDK:  rc.exe /fo res\JellyScale.res res\JellyScale.rc   (run inside res\)

"%FW%\csc.exe" /nologo /langversion:5 /target:winexe /platform:anycpu /optimize+ /warnaserror+ ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /win32res:res\JellyScale.res /resource:art\JellyScale.ico,JellyScale.ico /resource:art\JellyScale-128.png,JellyScale-128.png ^
  /out:build\JellyScale.exe src\*.cs
if errorlevel 1 exit /b 1

set NSIS=makensis
where makensis >nul 2>nul || set NSIS="%ProgramFiles(x86)%\NSIS\makensis.exe"
%NSIS% /V2 /WX Setup.nsi
if errorlevel 1 exit /b 1
echo Built build\JellyScale.exe and JellyScale-Setup.exe
