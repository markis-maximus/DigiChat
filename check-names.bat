@echo off
title DigiChat - check roster names
cd /d "%~dp0src\DigiChat.Overlay"
echo Checking data\lineages.json against data\digimon-names.json ...
echo.
node tools\check-names.mjs %*
if errorlevel 1 (
  echo.
  echo *** FAILED - the findings above need a human decision. Nothing was changed.
) else (
  echo.
  echo Roster check passed.
)
echo.
echo (Pass --refresh to re-download the reference list.)
echo Press any key to close.
pause >nul
