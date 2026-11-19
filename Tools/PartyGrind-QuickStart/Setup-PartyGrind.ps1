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
$pidPromptLines = New-Object System.Text.StringBuilder
$pidCheckLines = New-Object System.Text.StringBuilder
$duplicateCheckLines = New-Object System.Text.StringBuilder
$launchLines = New-Object System.Text.StringBuilder

for ($i = 0; $i -lt $ClientCount; $i++) {
    $n = $i + 1
    [void] $setLines.AppendLine(('set "NAME{0}={1}"' -f $n, $names[$i]))
    [void] $setLines.AppendLine(('set "ROLE{0}={1}"' -f $n, $roles[$i]))
    [void] $pidPromptLines.AppendLine(('set /p PID{0}=WoW PID for {1} (Client{0}, {2}): ' -f $n, $names[$i], $roles[$i]))
    [void] $pidCheckLines.AppendLine(('if "%PID{0}%"=="" goto :missing_pid' -f $n))
    # Pass the value through an environment variable: this avoids for-variable
    # substitution issues and is the same validation pattern the UI generates.
    [void] $pidCheckLines.AppendLine(('powershell -NoProfile -Command "$p=0; if ([int]::TryParse($env:PID{0},[ref]$p) -and $p -gt 0) {{ exit 0 }}; exit 1"' -f $n))
    [void] $pidCheckLines.AppendLine('if errorlevel 1 goto :bad_pid')
    for ($j = 0; $j -lt $i; $j++) {
        [void] $duplicateCheckLines.AppendLine(('if "%PID{0}%"=="%PID{1}%" goto :duplicate_pid' -f $n, ($j + 1)))
    }

    $isCoordinator = if ($i -eq 0) { 'true' } else { 'false' }
    [void] $launchLines.AppendLine(('echo Launching Client{0} ({1}, {2}) with WoW PID %PID{0}%' -f $n, $names[$i], $roles[$i]))
    $launch = 'start "Client{0} - {1}" "%BOT_DIR%\CopilotBuddy.exe" -pid=%PID{0}% -instance=Client{0} -botname="Party Grind" -loadprofile="%PROFILE%" -squadrole=%ROLE{0}% -squadcoordinator={2} -squadteam=%TEAM_ID% -squadport=%IPC_PORT% -squadleader="%NAME1%" -squadroster="%ROSTER%"'
    [void] $launchLines.AppendLine(($launch -f $n, $names[$i], $isCoordinator))
    if ($i -eq 0) {
        [void] $launchLines.AppendLine('timeout /t 3 /nobreak >nul')
    }
}

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

{{PID_PROMPTS}}

echo Validating PIDs...
{{PID_CHECKS}}

{{DUP_CHECKS}}

echo PID validation OK.

echo.
echo Starting coordinator, then followers. Leave each bot window open.
echo.
{{LAUNCH_LINES}}
echo.
echo All clients launched. Watch the bot log for IPC connection messages.
echo If no bot window appeared, check BOT_DIR\Logs and run CopilotBuddy.exe manually.
pause
exit /b 0

:missing_pid
echo [ERROR] Enter the WoW PID for every client. Do not leave any PID blank.
pause
exit /b 1

:bad_pid
echo [ERROR] One of the entered PIDs is not a positive integer.
pause
exit /b 1

:duplicate_pid
echo [ERROR] Every client must have a different WoW PID.
pause
exit /b 1
'@

$bat = $batTemplate.Replace('{{BOT_DIR}}', $botDirFull)
$bat = $bat.Replace('{{PROFILE}}', $profileFull)
$bat = $bat.Replace('{{TEAM_ID}}', $TeamId)
$bat = $bat.Replace('{{IPC_PORT}}', [string] $IpcPort)
$bat = $bat.Replace('{{CLIENT_COUNT}}', [string] $ClientCount)
$bat = $bat.Replace('{{SET_LINES}}', $setLines.ToString().TrimEnd())
$bat = $bat.Replace('{{ROSTER}}', $roster)
$bat = $bat.Replace('{{PID_PROMPTS}}', $pidPromptLines.ToString().TrimEnd())
$bat = $bat.Replace('{{PID_CHECKS}}', $pidCheckLines.ToString().TrimEnd())
$bat = $bat.Replace('{{DUP_CHECKS}}', $duplicateCheckLines.ToString().TrimEnd())
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
Write-Host '  3. Enter the WoW PID shown for each character. The launcher uses -pid, not the window title.'
Write-Host '  4. Check the bot log for "[PartyGrind] Started as ..." and IPC connection messages.'
