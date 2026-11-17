@echo off
rem Starts lider from the release kit (Windows): lub\bin\lider.cmd [Game.csproj] [player arguments...]
rem A Game.csproj argument becomes LIDER_GAME, the game lider opens.
rem lider reads its files (Lider.tcsb, the font, the kana-kanji dictionary) from the kit's lider\ (LIDER_HOME).
rem The comments are ASCII because cmd reads batch files in the console code page, not UTF-8.
setlocal
set "BIN=%~dp0"
for %%I in ("%BIN%..") do set "KIT=%%~fI"
if /i "%~x1"==".csproj" (
  set "LIDER_GAME=%~f1"
  shift
)
if not defined LIDER_HOME set "LIDER_HOME=%KIT%\lider"
set ARGS=
:args
if "%~1"=="" goto run
set ARGS=%ARGS% "%~1"
shift
goto args
:run
"%BIN%lider-player.exe" "%KIT%\lider\Lider.tcsb" %ARGS%
exit /b %ERRORLEVEL%
