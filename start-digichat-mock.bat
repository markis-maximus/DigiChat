@echo off
title DigiChat (MOCK - no Twitch)
cd /d "%~dp0"
set ASPNETCORE_ENVIRONMENT=Development
echo Starting DigiChat in MOCK mode (no Twitch; use the admin Dev panel to simulate chat)...
echo Admin:   http://localhost:5170/admin/
echo Overlay: http://localhost:5170/overlay/
echo.
dotnet run --project src\DigiChat.Api --no-launch-profile
echo.
echo DigiChat stopped. Press any key to close.
pause >nul
