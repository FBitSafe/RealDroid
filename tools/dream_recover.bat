@echo off
chcp 65001 >nul
cd /d "%~dp0.."

set GODOT=C:\Users\fbits\OneDrive\Desktop\Godot.NET\Godot_v4.7.2-stable_mono_win64_console.exe
set GENS=200
set CHIPARG=
set NOGIT=

if not "%~1"=="" set GENS=%~1
if not "%~2"=="" set CHIPARG=--chip %~2
if /i "%~3"=="nogit" set NOGIT=1

echo.
echo Son RECOVER, pokolenij: %GENS%. Ne zakryvaite okno. Otklyuchite spyashij rezhim.
echo.

"%GODOT%" --fixed-fps 30 --path . res://Dream.tscn -- --gens %GENS% --sector RECOVER --min-level 0.3 %CHIPARG%

if defined NOGIT goto :end

echo.
echo Otpravlyayu log v GitHub...
git add Diagnostics/DreamLogs
git commit -m "Dream RECOVER %GENS% gens log"
if errorlevel 1 (
    echo Ne udalos otpravit log v GitHub, otpravte vruchnuyu.
    goto :end
)
git push
if errorlevel 1 echo Ne udalos otpravit log v GitHub, otpravte vruchnuyu.

:end
echo.
pause