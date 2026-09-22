@echo off
rem Installs the Interception keyboard/mouse filter driver (oblitum/Interception v1.0.1).
rem Lets Airdeck remap the remotes' ordinary keys (arrows, digits, Pg+/Pg-, DEL, Menu)
rem without touching your real keyboard. Right-click this file > Run as administrator.
rem Afterwards restart Windows - the driver only works once loaded at boot. Undo with uninstall-interception.cmd.

net session >nul 2>&1
if errorlevel 1 (
    echo This needs administrator rights. Right-click the file and choose "Run as administrator".
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-interception.ps1"
if errorlevel 1 (
    echo Could not download Interception.
    pause
    exit /b 1
)

"%~dp0..\vendor\Interception\Interception\command line installer\install-interception.exe" /install
echo.
echo Done. Restart Windows to finish (until then the remotes' arrow/number keys may not respond).
pause
