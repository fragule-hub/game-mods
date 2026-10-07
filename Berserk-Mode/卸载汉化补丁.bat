@echo off
setlocal
REM Berserk Mode Chinese Fix v1.1 - Uninstaller
REM Removes every file installed by the patch. Game saves are NOT affected.

cd /d "%~dp0"

echo ==============================================
echo  Berserk Mode - Uninstall Chinese Fix v1.1
echo ==============================================
echo.
echo This will DELETE the following from this folder:
echo   - winhttp.dll
echo   - doorstop_config.ini
echo   - .doorstop_version
echo   - BepInEx  (entire folder)
echo.
echo Your game saves will NOT be affected.
echo.
choice /C YN /M "Proceed with uninstall? (Y=Yes / N=No)"
if errorlevel 2 goto :cancel

del /F /Q "winhttp.dll" >nul 2>&1
del /F /Q "doorstop_config.ini" >nul 2>&1
del /F /Q ".doorstop_version" >nul 2>&1
rmdir /S /Q "BepInEx" >nul 2>&1

echo.
echo Done. The patch has been removed.
echo If the game still launches with mods, delete the
echo remaining files listed above manually.
goto :end

:cancel
echo.
echo Cancelled. Nothing was deleted.

:end
echo.
pause
