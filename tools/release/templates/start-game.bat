@echo off
rem EraTD launcher (T-096): the shipped exe must NOT write to the pinned
rem development data root, so point the D-03 injected root at the
rem exe-adjacent "data" folder before starting the game.
setlocal
set "ERA_DATA_ROOT=%~dp0data"
if not exist "%ERA_DATA_ROOT%" mkdir "%ERA_DATA_ROOT%"
start "" "%~dp0EraTD.exe" %*
endlocal
