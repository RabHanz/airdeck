@echo off
set EXE=%~dp0airdeck.exe
if not exist "%EXE%" set EXE=%~dp0tools\bin\airdeck.exe
rem Stops Airdeck. Every remote immediately goes back to its stock behaviour (same as Ctrl+Alt+Shift+F12).
"%EXE%" --exit
if errorlevel 1 taskkill /im airdeck.exe /f >nul 2>&1
echo Airdeck stopped - remotes are back to stock.
timeout /t 2 >nul
