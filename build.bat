@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find the .NET Framework 4 C# compiler.
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:OBSNetworkViewer.exe ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /r:System.Web.Extensions.dll /r:System.Security.dll ^
    src\*.cs
if errorlevel 1 exit /b 1

echo Built OBSNetworkViewer.exe
