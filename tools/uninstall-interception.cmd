@echo off
rem Removes the Interception filter driver. Right-click > Run as administrator, then reboot.

net session >nul 2>&1
if errorlevel 1 (
    echo This needs administrator rights. Right-click the file and choose "Run as administrator".
    pause
    exit /b 1
)

"%~dp0..\vendor\Interception\Interception\command line installer\install-interception.exe" /uninstall
echo.
echo Done. Reboot Windows to finish removing the driver.
pause
