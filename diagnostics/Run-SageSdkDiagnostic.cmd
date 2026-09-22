@echo off
setlocal
title Rutter Sage 50 SDK Diagnostic
echo Rutter Sage 50 SDK Diagnostic v2
if not exist "%~dp0Compare-SageSdk.ps1" (
    echo Please right-click the ZIP, choose Extract All, and run this from the extracted folder.
    echo Keep Compare-SageSdk.ps1 beside this launcher.
    pause
    exit /b 1
)
echo.
echo Exit the Rutter connector from its tray icon before continuing.
echo Sage can remain open. This does not open or change company data.
echo Reports include company names and paths, but no accounting records or credentials.
echo Results will be saved in a SageSdkDiagnostic folder on your Desktop.
echo.
pause
set "SageDiagnosticPowerShell=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "SageDiagnosticPowerShell=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%SageDiagnosticPowerShell%" -NoProfile -File "%~dp0Compare-SageSdk.ps1" %*
if errorlevel 1 (
    echo.
    echo The diagnostic could not finish. Please send Rutter the error above.
    echo If your organization blocks scripts, ask IT to approve this diagnostic.
)
echo.
echo Please send Rutter the report folder shown above, or a screenshot of any error.
pause
endlocal
