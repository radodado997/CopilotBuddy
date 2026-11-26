[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BotDir,

    [Parameter(Mandatory = $true)]
    [string] $ProfilePath,

    [string] $TeamId = 'Squad1',

    [ValidateRange(1024, 65535)]
    [int] $IpcPort = 1339,

    [ValidateRange(3, 5)]
    [int] $ClientCount = 3,

    [string[]] $CharacterNames = @('TankChar', 'HealerChar', 'DpsChar'),
    [string[]] $Roles = @('TankLeader', 'Healer', 'DpsMelee'),

    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Assert-SafeToken {
    param([string] $Value, [string] $Label)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label cannot be empty."
    }
    # These characters would break the generated .bat file or the CSV roster.
    if ($Value -match '[\s"%!&|<>^(),;\\]') {
        throw "$Label contains an unsupported character: $Value"
    }
}

if (-not (Test-Path -LiteralPath $BotDir -PathType Container)) {
    throw "BotDir does not exist: $BotDir"
}
if (-not (Test-Path -LiteralPath $ProfilePath -PathType Leaf)) {
    throw "ProfilePath does not exist: $ProfilePath"
}
if (-not (Test-Path -LiteralPath (Join-Path $BotDir 'CopilotBuddy.exe') -PathType Leaf)) {
    throw "CopilotBuddy.exe was not found in BotDir: $BotDir"
}

$botDirFull = (Resolve-Path -LiteralPath $BotDir).Path.TrimEnd([char[]] @('\', '/'))
$profileFull = (Resolve-Path -LiteralPath $ProfilePath).Path

Assert-SafeToken -Value $TeamId -Label 'TeamId'

# Accept both PowerShell forms: -CharacterNames A,B,C and -CharacterNames "A,B,C".
function Convert-ToNameList {
    param([string[]] $Values)
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($value in $Values) {
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        foreach ($part in ($value -split '[,;]')) {
            $trimmed = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                [void] $list.Add($trimmed)
            }
        }
    }
    return $list.ToArray()
}

$names = @(Convert-ToNameList -Values $CharacterNames)
$roles = @(Convert-ToNameList -Values $Roles)
if ($names.Count -lt $ClientCount) {
    throw "Expected at least $ClientCount character names, got $($names.Count)."
}
if ($roles.Count -lt $ClientCount) {
    throw "Expected at least $ClientCount roles, got $($roles.Count)."
}

$names = @($names | Select-Object -First $ClientCount)
$roles = @($roles | Select-Object -First $ClientCount)
$validRoles = @('TankLeader', 'Healer', 'DpsMelee', 'DpsRanged', 'HybridOffHeal')
$seen = @{}

for ($i = 0; $i -lt $ClientCount; $i++) {
    Assert-SafeToken -Value $names[$i] -Label "CharacterNames[$i]"
    Assert-SafeToken -Value $roles[$i] -Label "Roles[$i]"
    if ($seen.ContainsKey($names[$i])) {
        throw "Duplicate character name: $($names[$i])"
    }
    $seen[$names[$i]] = $true
    if ($roles[$i] -notin $validRoles) {
        throw "Invalid role at slot $($i + 1): $($roles[$i]). Valid roles: $($validRoles -join ', ')"
    }
}
if ($roles[0] -ne 'TankLeader') {
    throw 'Client1 must be TankLeader because the generated launcher marks it as coordinator.'
}

# 1. Random per-character secrets. The bot requires at least 32 decoded bytes.
$secretsDir = Join-Path $botDirFull 'Secrets'
$secretsPath = Join-Path $secretsDir 'PartyGrindSecrets.json'
if (-not (Test-Path -LiteralPath $secretsDir -PathType Container)) {
    New-Item -ItemType Directory -Path $secretsDir -Force | Out-Null
}
if ((Test-Path -LiteralPath $secretsPath) -and (-not $Force)) {
    throw "$secretsPath already exists. Re-run with -Force to regenerate all secrets."
}

$secrets = @{}
foreach ($name in $names) {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $secrets[$name] = [Convert]::ToBase64String($bytes)
}
Set-Content -LiteralPath $secretsPath -Value ($secrets | ConvertTo-Json -Compress) -Encoding UTF8

# 2. Per-instance SquadSettings.xml. CLI overrides handle role/team/port/roster;
#    the secrets path is the only value that must exist on disk before launch.
$escapedSecretsPath = [System.Security.SecurityElement]::Escape($secretsPath)
for ($i = 0; $i -lt $ClientCount; $i++) {
    $settingsDir = Join-Path $botDirFull ("Settings\Instances\Client{0}" -f ($i + 1))
    if (-not (Test-Path -LiteralPath $settingsDir -PathType Container)) {
        New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
    }
    $settingsPath = Join-Path $settingsDir ("SquadSettings_{0}.xml" -f $names[$i])
    if ((Test-Path -LiteralPath $settingsPath) -and (-not $Force)) {
        throw "$settingsPath already exists. Re-run with -Force to overwrite instance settings."
    }
    $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<SquadSettings>`n  <IpcMemberSecretsPath>$escapedSecretsPath</IpcMemberSecretsPath>`n</SquadSettings>`n"
    Set-Content -LiteralPath $settingsPath -Value $xml -Encoding UTF8
}

# 3. Launcher. Character names, roles and the profile path come from the same
#    parameters used above, so they cannot drift out of sync.
$setLines = New-Object System.Text.StringBuilder
$launchLines = New-Object System.Text.StringBuilder
# PIDs are entered in one line ("1111 2222 3333"), or passed straight to the
# .bat as arguments. %~1..%~5 are collected into one string for validation.
$argSlots = New-Object System.Text.StringBuilder
$pidReadLines = New-Object System.Text.StringBuilder
$tokenLetters = @('a', 'b', 'c', 'd', 'e')

for ($i = 0; $i -lt $ClientCount; $i++) {
    $n = $i + 1
    [void] $setLines.AppendLine(('set "NAME{0}={1}"' -f $n, $names[$i]))
    [void] $setLines.AppendLine(('set "ROLE{0}={1}"' -f $n, $roles[$i]))
    [void] $argSlots.Append((' %~{0}' -f $n))

    $isCoordinator = if ($i -eq 0) { 'true' } else { 'false' }
    [void] $launchLines.AppendLine(('echo Launching Client{0} ({1}, {2}) with WoW PID %PID{0}%' -f $n, $names[$i], $roles[$i]))
    $launch = 'start "Client{0} - {1}" "%BOT_DIR%\CopilotBuddy.exe" -pid=%PID{0}% -instance=Client{0} -botname="Party Grind" -loadprofile="%PROFILE%" -squadrole=%ROLE{0}% -squadcoordinator={2} -squadteam=%TEAM_ID% -squadport=%IPC_PORT% -squadleader="%NAME1%" -squadroster="%ROSTER%"'
    [void] $launchLines.AppendLine(($launch -f $n, $names[$i], $isCoordinator))
    if ($i -eq 0) {
        [void] $launchLines.AppendLine('timeout /t 3 /nobreak >nul')
    }
}

# PIDs: one prompt for all clients (or straight from %~1..%~n), validated and
# split into %PID1%..%PIDn% by a single PowerShell call, so the .bat stays short.
$order = $names -join ', '
[void] $pidReadLines.AppendLine('set "PID_OUT=%TEMP%\PartyGrind_pids_%RANDOM%.txt"')
[void] $pidReadLines.AppendLine('set "FROM_ARGS="')
[void] $pidReadLines.AppendLine('set "RAW="')
[void] $pidReadLines.AppendLine('if not "%~1"=="" (')
[void] $pidReadLines.AppendLine('    set "FROM_ARGS=1"')
[void] $pidReadLines.AppendLine(('    set "RAW=%s"' -f $argSlots.ToString().TrimStart()))
[void] $pidReadLines.AppendLine(')')
[void] $pidReadLines.AppendLine('')
[void] $pidReadLines.AppendLine(':PID_PROMPT')
[void] $pidReadLines.AppendLine('if defined FROM_ARGS goto PID_VALIDATE')
[void] $pidReadLines.AppendLine('echo.')
[void] $pidReadLines.AppendLine(('echo PID order = client order: {0}' -f $order))
[void] $pidReadLines.AppendLine('set "RAW="')
[void] $pidReadLines.AppendLine(('set /p RAW=Enter {0} WoW PIDs (space or comma separated): ' -f $ClientCount))
[void] $pidReadLines.AppendLine('')
[void] $pidReadLines.AppendLine(':PID_VALIDATE')
[void] $pidReadLines.AppendLine('if not defined RAW (')
[void] $pidReadLines.AppendLine(('    echo [ERROR] Enter all {0} WoW PIDs on one line.' -f $ClientCount))
[void] $pidReadLines.AppendLine('    set "FROM_ARGS="')
[void] $pidReadLines.AppendLine('    goto PID_PROMPT')
[void] $pidReadLines.AppendLine(')')
[void] $pidReadLines.AppendLine('REM One check for the whole list: count, positive integers, no duplicates.')
[void] $pidReadLines.AppendLine('powershell -NoProfile -Command ^')
[void] $pidReadLines.AppendLine('  "$raw = @([string]$env:RAW -split ''[\s,;]+'' | Where-Object { $_ -ne '''' });" ^')
[void] $pidReadLines.AppendLine(('  "if ($raw.Count -ne {0}) {{ Write-Host (''[ERROR] Expected {0} PIDs, got '' + $raw.Count + ''.''); exit 1 }};"' -f $ClientCount))
[void] $pidReadLines.AppendLine('  "$pids = @(); foreach ($r in $raw) { $p = 0; if (-not [int]::TryParse($r, [ref]$p) -or $p -le 0) { Write-Host (''[ERROR] Not a valid PID: '' + $r); exit 1 }; $pids += $p };" ^')
[void] $pidReadLines.AppendLine(('  "if (($pids | Sort-Object -Unique).Count -ne {0}) {{ Write-Host ''[ERROR] Every client needs a different WoW PID.''; exit 1 }};"' -f $ClientCount))
[void] $pidReadLines.AppendLine('  "Set-Content -LiteralPath $env:PID_OUT -Value ($pids -join '' '') -Encoding ASCII"')
[void] $pidReadLines.AppendLine('if errorlevel 1 (')
[void] $pidReadLines.AppendLine('    set "RAW="')
[void] $pidReadLines.AppendLine('    set "FROM_ARGS="')
[void] $pidReadLines.AppendLine('    goto PID_PROMPT')
[void] $pidReadLines.AppendLine(')')
for ($i = 0; $i -lt $ClientCount; $i++) {
    [void] $pidReadLines.AppendLine(('set "PID{0}="' -f ($i + 1)))
}
$tokens = (1..$ClientCount) -join ','
[void] $pidReadLines.AppendLine(('for /f "usebackq tokens=%s" %%%%a in ("%%PID_OUT%%") do (' -f $tokens))
for ($i = 0; $i -lt $ClientCount; $i++) {
    [void] $pidReadLines.AppendLine(('    set "PID{0}=%%%{1}"' -f ($i + 1), $tokenLetters[$i]))
}
[void] $pidReadLines.AppendLine(')')
[void] $pidReadLines.AppendLine('del "%PID_OUT%" >nul 2>&1')
[void] $pidReadLines.AppendLine('echo.')
$pidSummary = @()
for ($i = 0; $i -lt $ClientCount; $i++) {
    $pidSummary += ('{0}=%PID{1}%' -f $names[$i], ($i + 1))
}
[void] $pidReadLines.AppendLine(('echo PIDs: {0}' -f ($pidSummary -join '  ')))
[void] $pidReadLines.AppendLine('echo.')

$roster = $names -join ','
$batTemplate = @'
@echo off
setlocal EnableExtensions
REM Generated by Setup-PartyGrind.ps1. Do not paste secrets here: -pid binds the client.
set "BOT_DIR={{BOT_DIR}}"
set "PROFILE={{PROFILE}}"
set "TEAM_ID={{TEAM_ID}}"
set "IPC_PORT={{IPC_PORT}}"
set "CLIENT_COUNT={{CLIENT_COUNT}}"
{{SET_LINES}}
set "ROSTER={{ROSTER}}"

if not exist "%BOT_DIR%\CopilotBuddy.exe" (
    echo [ERROR] CopilotBuddy.exe not found: "%BOT_DIR%\CopilotBuddy.exe"
    exit /b 1
)
if not exist "%PROFILE%" (
    echo [ERROR] Profile not found: "%PROFILE%"
    exit /b 1
)

echo.
echo WoW processes. MainWindowTitle is only a hint - the PID you enter is the attachment selector.
powershell -NoProfile -Command "Get-Process Wow -ErrorAction SilentlyContinue | Sort-Object StartTime | Format-Table Id,StartTime,MainWindowTitle -AutoSize"
echo.
echo You can also pass all PIDs as arguments: Start-PartyGrind.bat {{PID_EXAMPLE}}
echo.

{{PID_READ}}

echo Starting coordinator, then followers. Leave each bot window open.
echo.
{{LAUNCH_LINES}}
echo.
echo All clients launched. Watch the bot log for IPC connection messages.
echo If no bot window appeared, check BOT_DIR\Logs and run CopilotBuddy.exe manually.
pause
exit /b 0
'@

$bat = $batTemplate.Replace('{{BOT_DIR}}', $botDirFull)
$bat = $bat.Replace('{{PROFILE}}', $profileFull)
$bat = $bat.Replace('{{TEAM_ID}}', $TeamId)
$bat = $bat.Replace('{{IPC_PORT}}', [string] $IpcPort)
$bat = $bat.Replace('{{CLIENT_COUNT}}', [string] $ClientCount)
$bat = $bat.Replace('{{SET_LINES}}', $setLines.ToString().TrimEnd())
$bat = $bat.Replace('{{ROSTER}}', $roster)
$pidExample = ((1..$ClientCount) | ForEach-Object { '110' + $_ }) -join ' '
$bat = $bat.Replace('{{PID_EXAMPLE}}', $pidExample)
$bat = $bat.Replace('{{PID_READ}}', $pidReadLines.ToString().TrimEnd())
$bat = $bat.Replace('{{LAUNCH_LINES}}', $launchLines.ToString().TrimEnd())

$launcherPath = Join-Path $botDirFull 'Start-PartyGrind.bat'
if ((Test-Path -LiteralPath $launcherPath) -and (-not $Force)) {
    throw "$launcherPath already exists. Re-run with -Force to overwrite it."
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($launcherPath, ($bat + "`r`n"), $utf8NoBom)

Write-Host ''
Write-Host 'PartyGrind quick-start setup complete.' -ForegroundColor Green
Write-Host "Secrets: $secretsPath"
Write-Host "Launcher: $launcherPath"
Write-Host ''
Write-Host 'Next steps:'
Write-Host '  1. Make sure all WoW 3.3.5a (build 12340) clients are logged in to the configured characters.'
Write-Host '  2. Run Start-PartyGrind.bat from the bot folder.'
Write-Host '  3. Enter all WoW PIDs in one line (space or comma separated), in client order.'
Write-Host '  4. Check the bot log for "[PartyGrind] Started as ..." and IPC connection messages.'
