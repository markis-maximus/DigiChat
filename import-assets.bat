@echo off
title DigiChat - import sprite art
cd /d "%~dp0src\DigiChat.Overlay"
echo Scanning public\assets\sprites\ ...
node tools\import-assets.mjs
if errorlevel 1 (
  echo.
  echo *** FAILED - the manifest was not written. The overlay is unchanged.
  goto :done
)
echo Rebuilding the overlay...
call npm run build
if errorlevel 1 (
  echo.
  echo *** FAILED - the manifest was written but the overlay did NOT rebuild,
  echo     so the served overlay is stale. Fix the error above and run this again
  echo     before streaming.
  goto :done
)
echo.
echo Done. Refresh the OBS Browser Source to see the new art.
:done
echo.
echo Press any key to close.
pause >nul
