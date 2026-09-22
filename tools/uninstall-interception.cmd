@echo off
rem Removes the Interception filter driver. Right-click > Run as administrator.
rem Afterwards unplug and re-plug each remote's USB receiver: its keys work normally again
rem immediately (the leftover driver file is cleaned up at the next restart).

net session >nul 2>&1
if errorlevel 1 (
    echo This needs administrator rights. Right-click the file and choose "Run as administrator".
    pause
    exit /b 1
)

"%~dp0..\vendor\Interception\Interception\command line installer\install-interception.exe" /uninstall
echo.
echo Done. Now unplug and re-plug each remote's USB receiver - no restart needed.
pause
