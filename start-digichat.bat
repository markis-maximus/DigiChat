@echo off
title DigiChat (LIVE - real Twitch)
cd /d "%~dp0"
set ASPNETCORE_ENVIRONMENT=Production
echo Starting DigiChat in LIVE mode (real Twitch connection)...
echo Admin:   http://localhost:5170/admin/
echo Overlay: http://localhost:5170/overlay/  (this is the OBS Browser Source URL)
echo.
dotnet run -c Release --project src\DigiChat.Api --no-launch-profile
echo.
echo DigiChat stopped. Press any key to close.
pause >nul
