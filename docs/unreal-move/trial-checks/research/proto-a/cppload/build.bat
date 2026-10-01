@echo off
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "%~dp0"
cl /nologo /EHsc /O2 load.cpp /Fe:load.exe >build.log 2>&1
echo cl_exit=%ERRORLEVEL%
.\load.exe "C:\Users\MD_Ki\AppData\Local\Temp\claude\D--Projects-Project-Chimera\6de76155-94ab-440a-84fd-59aa5580f954\scratchpad\aotsmoke\out\aotsmoke.dll"
echo run_exit=%ERRORLEVEL%
