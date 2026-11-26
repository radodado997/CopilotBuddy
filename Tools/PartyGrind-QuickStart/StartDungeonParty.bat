@echo off
setlocal EnableExtensions
REM CopilotBuddy PartyGrind 5-Client Launcher
REM PID-y podajesz JEDNYM wpisem: "1111 2222 3333 4444 5555"
REM (spacje, przecinki, sredniki - dowolnie), albo od razu jako argumenty:
REM   StartDungeonParty.bat 1111 2222 3333 4444 5555
set TEAM_ID=Squad1
set IPC_PORT=1339

set "ROSTER=Bedogie,Ksionc,Belmondo,Sentino,Bedoes"
set "NAMES=Bedogie  Ksionc  Belmondo  Sentino  Bedoes"
set "PID_OUT=%TEMP%\DungeonParty_pids_%RANDOM%.txt"
set "FROM_ARGS="
set "RAW="

if not "%~1"=="" (
    set "FROM_ARGS=1"
    set "RAW=%~1 %~2 %~3 %~4 %~5"
)

:PID_PROMPT
if defined FROM_ARGS goto PID_VALIDATE
echo.
echo Kolejnosc PID-ow = kolejnosc postaci:
echo    1. Bedogie  2. Ksionc  3. Belmondo  4. Sentino  5. Bedoes
set "RAW="
set /p "RAW=Podaj 5 PID-ow (spacja lub przecinek): "

:PID_VALIDATE
if not defined RAW (
    echo [ERROR] Nie podano zadnego PID-a. Podaj 5 PID-ow.
    set "FROM_ARGS="
    goto PID_PROMPT
)

REM Jedna walidacja dla wszystkich: liczba, dodatnie liczby calkowite, brak duplikatow.
powershell -NoProfile -Command ^
  "$raw = @([string]$env:RAW -split '[\s,;]+' | Where-Object { $_ -ne '' });" ^
  "if ($raw.Count -ne 5) { Write-Host ('[ERROR] Potrzeba dokladnie 5 PID-ow, dostalem ' + $raw.Count + '.'); exit 1 };" ^
  "$pids = @(); foreach ($r in $raw) { $p = 0; if (-not [int]::TryParse($r, [ref]$p) -or $p -le 0) { Write-Host ('[ERROR] To nie jest poprawny PID: ' + $r); exit 1 }; $pids += $p };" ^
  "if (($pids | Sort-Object -Unique).Count -ne 5) { Write-Host '[ERROR] PID-y musza byc rozne - kazde okno WoW ma inny PID.'; exit 1 };" ^
  "Set-Content -LiteralPath $env:PID_OUT -Value ($pids -join ' ') -Encoding ASCII"
if errorlevel 1 (
    set "RAW="
    set "FROM_ARGS="
    goto PID_PROMPT
)

set "PID1="
set "PID2="
set "PID3="
set "PID4="
set "PID5="
for /f "usebackq tokens=1,2,3,4,5" %%a in ("%PID_OUT%") do (
    set "PID1=%%a"
    set "PID2=%%b"
    set "PID3=%%c"
    set "PID4=%%d"
    set "PID5=%%e"
)
del "%PID_OUT%" >nul 2>&1

echo.
echo PID-y: Bedogie=%PID1%  Ksionc=%PID2%  Belmondo=%PID3%  Sentino=%PID4%  Bedoes=%PID5%
echo.

start "Client1 - Bedogie" CopilotBuddy.exe -pid=%PID1% -instance=Client1 -botname="Party Grind" -squadmode=Dungeon -dungeonentry=LfgSpecificByBracket -dungeonids="163" -squadrole=TankLeader -squadcoordinator=true -squadteam=%TEAM_ID% -squadport=%IPC_PORT% -squadleader="Bedogie" -squadroster="%ROSTER%"
timeout /t 2 /nobreak >nul
start "Client2 - Ksionc" CopilotBuddy.exe -pid=%PID2% -instance=Client2 -botname="Party Grind" -squadmode=Dungeon -dungeonentry=LfgSpecificByBracket -dungeonids="163" -squadrole=Healer -squadcoordinator=false -squadteam=%TEAM_ID% -squadport=%IPC_PORT% -squadleader="Bedogie" -squadroster="%ROSTER%"
start "Client3 - Belmondo" CopilotBuddy.exe -pid=%PID3% -instance=Client3 -botname="Party Grind" -squadmode=Dungeon -dungeonentry=LfgSpecificByBracket -dungeonids="163" -squadrole=DpsMelee -squadcoordinator=false -squadteam=%TEAM_ID% -squadport=%IPC_PORT% -squadleader="Bedogie" -squadroster="%ROSTER%"
start "Client4 - Sentino" CopilotBuddy.exe -pid=%PID4% -instance=Client4 -botname="Party Grind" -squadmode=Dungeon -dungeonentry=LfgSpecificByBracket -dungeonids="163" -squadrole=DpsRanged -squadcoordinator=false -squadteam=%TEAM_ID% -squadport=%IPC_PORT% -squadleader="Bedogie" -squadroster="%ROSTER%"
start "Client5 - Bedoes" CopilotBuddy.exe -pid=%PID5% -instance=Client5 -botname="Party Grind" -squadmode=Dungeon -dungeonentry=LfgSpecificByBracket -dungeonids="163" -squadrole=DpsRanged -squadcoordinator=false -squadteam=%TEAM_ID% -squadport=%IPC_PORT% -squadleader="Bedogie" -squadroster="%ROSTER%"

echo Wszystkie 5 klientow wystartowalo. Zostaw okna botow otwarte.
endlocal
exit /b 0
