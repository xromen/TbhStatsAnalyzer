@echo off
setlocal
if defined VSCMD_ARG_TGT_ARCH goto compilerReady
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "TBH_VS_PATH=%%i"
if not defined TBH_VS_PATH (
  echo MSVC x64 not found. Install Visual Studio Desktop development with C++.
  exit /b 1
)
call "%TBH_VS_PATH%\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
:compilerReady
cd /d "%~dp0"
if not exist build mkdir build
cl /nologo /c /O2 /MT /DWIN32_LEAN_AND_MEAN /D_WIN64 /Ivendor\minhook\include /Fobuild\ vendor\minhook\src\buffer.c vendor\minhook\src\hook.c vendor\minhook\src\trampoline.c vendor\minhook\src\hde\hde64.c
if errorlevel 1 exit /b %errorlevel%
cl /nologo /LD /std:c++17 /O2 /MT /EHsc /utf-8 /Ivendor\minhook\include /Fobuild\ /Febuild\TbhObserver.dll Observer.cpp build\buffer.obj build\hook.obj build\trampoline.obj build\hde64.obj /link /INCREMENTAL:NO Advapi32.lib
exit /b %errorlevel%
