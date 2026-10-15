@echo off
setlocal

rem ---- Set the program to run here ----
set "PROGRAM=C:\scripts\iPhoneVideoBackup\bin\Release\net48\publish\iPhoneVideoBackup.exe /dest H:\Steven\iphone /device iphone /y"
rem -------------------------------------

echo Press any key to cancel, starting in 10 seconds...
echo.

rem Count down from 10, checking for a keypress every 50 ms. Any key cancels
rem (exit code 1); otherwise the countdown finishes with exit code 0. Without an
rem interactive console the keypress check is skipped and the countdown just runs.
powershell -NoProfile -Command "$canRead = $true; try { [void][Console]::KeyAvailable } catch { $canRead = $false }; for ($i = 10; $i -gt 0; $i--) { Write-Host \"Starting in $i seconds...\"; $end = (Get-Date).AddSeconds(1); while ((Get-Date) -lt $end) { if ($canRead -and [Console]::KeyAvailable) { [void][Console]::ReadKey($true); exit 1 }; Start-Sleep -Milliseconds 50 } }; exit 0"
if errorlevel 1 (
    echo.
    echo Cancelled.
    exit /b 1
)

echo.
echo Starting program...
start "" %PROGRAM%
endlocal