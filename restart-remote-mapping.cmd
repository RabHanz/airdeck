@echo off
set EXE=%~dp0airdeck.exe
if not exist "%EXE%" set EXE=%~dp0tools\bin\airdeck.exe
rem Restarts Airdeck in the tray with the profiles you last used.
"%EXE%" --exit
timeout /t 2 >nul
start "" "%EXE%" --tray
echo Airdeck restarted.
timeout /t 2 >nul
