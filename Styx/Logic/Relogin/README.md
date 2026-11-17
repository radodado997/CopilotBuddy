# Auto-Relogin

Opt-in feature: when the WoW client crashes or exits while CopilotBuddy is running, the bot
relaunches the client, logs back in, selects the configured character, waits for the in-world
state, and only then resumes the previously active routine/botbase and profile.

Off by default. With the option off, no monitor thread is created and every existing code path
behaves exactly as before.

---

## 1. What already existed (and what the feature reuses)

| Concern | Existing mechanism | Used by auto-relogin |
| --- | --- | --- |
| Client death detection | `Process.HasExited` on `ObjectManager.WoWProcess` (`TreeRoot.RunTickBody`, `TreeRoot.MinimizeGuardLoop`) | same check, on a dedicated monitor thread |
| "In world" flag | `g_ClientConnectionState` `0x00BD0792` via `ObjectManager.IsInGame` / `StyxWoW.IsInGame` | same byte, read directly so it works while *not* attached |
| Attach + EndScene hook | `ObjectManager.Initialize(memory)` + `HookEndscene` (`MainWindow.FindAndAttachToWoW`) | same call, reused for the relaunched client |
| Lua input | `Lua.DoString` / `Lua.GetReturnValues` (injected EndScene, FASM) | same pipeline, no new input layer |
| Settings storage | `Styx.Helpers.Settings` + `[Setting]` / `[DefaultValue]`, one XML per class in `Settings/` | new `ReloginSettings` class, `Settings/ReloginSettings.xml` |
| Active dynamic profile | `LevelbotSettings.LastUsedPath` (written by `ProfileManager.LoadNew(path, true)`) and `CharacterSettings.LastUsedPath` | reloaded before the bot restarts |
| Active botbase | `BotManager.Current` (plus `CharacterSettings.SelectedBotIndex`) | left untouched; resume uses `StartBot()` |
| Logging | `Styx.Helpers.Logging.Write(...)` | all relogin events are logged there (UI + `Logs/*.log`) |

### Login/character-selection state from memory

The client stores the current glue screen as an ASCII name at **`0x00B6A9E0`** (`byte_B6A9E0`,
pushed by `sub_4D82C0` = `CGlueMgr::UpdateCurrentScreen`). Values seen on 3.3.5a:
`login`, `charselect`, `charcreate`, `realmwizard`, `patchdownload`, `credits`, `movie`,
`trialconvert`. This is the same address the sibling launcher (`Likon69/HBRelog`,
`GlueStateOffset`) reads for this exact client build.

Added to the existing offset table as `GlobalOffsets.GlueScreenString` — no offset was hardcoded
in the relogin code.

In-world state stays on `g_ClientConnectionState` (`0x00BD0792`), which the bot already reads.
Both are read through a **short-lived** `Memory` handle (`ReloginClient.Probe`), so detection
still works when the bot is not attached — which is the normal state after a crash.

### Input mechanism

No new mechanism was introduced. WotLK 3.3.5a has no Battle.net layer: the login form is driven
with `DefaultServerLogin(user, pass)`, the realm list with `ChangeRealm(i, j)`, character
selection with `CharacterSelect_SelectCharacter(i)` + `EnterWorld()`. Those are called from Lua
executed through the bot's existing injected-EndScene executor (`Lua.DoString`), the same path
every in-game action already uses. The chunks are modelled on HBRelog's login scripts for this
client, with the account name / character name escaped as Lua strings and the password byte-escaped
(`\ddd` per UTF-8 byte, HBRelog's `EncodeToUTF8`) so non-ASCII passwords survive.

---

## 2. Settings

New file **`Settings/ReloginSettings.xml`**, class `Styx.Helpers.ReloginSettings`
(global, like `StyxSettings.xml` / `UISettings.xml` — the credentials are per account, not per
character, and the file must exist before the bot has ever attached).

| Setting (XML element) | UI | Default | Meaning |
| --- | --- | --- | --- |
| `Enabled` | "Auto-relogin on crash" checkbox | `false` | master switch |
| `AccountName` | Account | `""` | 3.3.5a account name (no realm suffix) |
| `Password` | Password (`PasswordBox`) | `""` | account password |
| `RealmName` | Realm | `""` | realm as shown in the realm list; empty = leave the realm alone |
| `CharacterName` | Character | `""` | character to select before `EnterWorld()` |
| `WowExecutablePath` | Wow.exe path (+ browse) | `""` | used only when no client process exists |
| `RetryDelaySeconds` | Retry delay (s) | `30` | wait between attempts |
| `MaxRetryCount` | Max retries | `3` | attempts per crash before giving up |
| `LoginScreenTimeoutSeconds` | (file only) | `120` | wait for a readable glue screen / for login to complete |
| `InWorldTimeoutSeconds` | (file only) | `180` | wait for `g_ClientConnectionState` + `LocalPlayer` after `EnterWorld` |
| `HangGraceSeconds` | (file only) | `45` | sustained non-responsiveness before a hang counts as a crash; `0` disables hang detection |
| `LoginRejectSeconds` | (file only) | `20` | a login that fails faster than this *and* never reached the connecting state is a credential rejection, not an unreachable server |

### Storage format — plain text

The project has an `EncryptedAttribute` (`Styx/Helpers/EncryptedAttribute.cs`), but **no settings
class in this repository uses it** and there is no OS credential store integration anywhere
(`grep EncryptedAttribute` finds the attribute definition only). There is therefore no existing
encrypted store to adopt.

The credentials are stored **in plain text**, matching every other profile value the project
persists (`MailRecipient`, `LastUsedPath`, `Settings/CharacterSettings_*.xml` as a whole):

```xml
<ReloginSettings>
  <Enabled>True</Enabled>
  <AccountName>myaccount</AccountName>
  <Password>hunter2</Password>
  <RealmName>MyRealm</RealmName>
  <CharacterName>Mytoon</CharacterName>
  <WowExecutablePath>C:\WoW\Wow.exe</WowExecutablePath>
  <RetryDelaySeconds>30</RetryDelaySeconds>
  <MaxRetryCount>3</MaxRetryCount>
  <LoginScreenTimeoutSeconds>120</LoginScreenTimeoutSeconds>
  <InWorldTimeoutSeconds>180</InWorldTimeoutSeconds>
  <HangGraceSeconds>45</HangGraceSeconds>
  <LoginRejectSeconds>20</LoginRejectSeconds>
</ReloginSettings>
```

> Note the UI panel says the same thing ("Credentials are saved in plain text in
> Settings/ReloginSettings.xml"). If an encrypted store is added to the project later,
> `EncryptedAttribute` is ready to be applied to `Password` — it derives from `SettingAttribute`,
> but `Settings.GetXML`/`LoadFromXML` do not currently encrypt/decrypt, so using it would require
> changing the shared serializer (out of scope for this feature).

---

## 3. Detection logic

`ReloginManager.MonitorLoop` (background thread, 1 Hz, `IsBackground = true`). It only runs while
`Enabled` is true; `Start(...)` returns without creating a thread otherwise.

Three triggers, each logged with its reason:

1. **`ProcessExited`** — the tracked process is gone (`HasExited`). This is the only unambiguous
   crash/exit signal and it is what a client crash produces.
2. **`ClientHung`** — `Process.Responding` is false **continuously** for
   `max(HangGraceSeconds, 20s)` (20 s is the threshold HBRelog uses for "WoW is not responding").
   The stopwatch is reset the moment the window answers again, so a brief stall never fires it.
   The frozen client is closed (`CloseMainWindow` → `Kill`) before relaunching.
3. **`ClientLoggedOut`** — the client dropped out of the world to `login`/`charselect` **and stayed
   there for 5 s**, **and** the bot was running at that moment (`TreeRoot.IsRunning`).

The logout transition is tracked with an explicit `pendingLogout` state rather than by comparing
against the previous poll. `lastInWorld` is still updated every poll, but the confirmation timer
is owned by the pending state, which is what lets it accumulate across the polls that follow the
transition. It is cleared when the client is back in the world, when the bot is stopped, when the
loading screen is up, and when recovery starts.

### Why lag / a normal logout never triggers it

* **Memory-read failures are not a trigger.** `ReadProcessMemory` returning false (lag, a page not
  resident, a GC pause in the client) only produces `GlueKind.Unknown` for that one poll. The
  logout trigger needs five *consecutive* seconds of a *named* glue screen; the hang trigger uses
  the window's `Responding` state, not memory reads, and needs a sustained stall.
* **Loading screens are read explicitly.** `g_ClientConnectionState` drops to 0 while a loading
  screen is up, so the probe also reads `IsLoadingOrConnecting` (`0x00B6AA38`). Those polls reset
  the logout timer instead of counting toward it, which is what keeps zoning and the initial
  loading screen out of the detector entirely. (EnterWorld → loading → in-world never shows a
  stable `login` screen, so it cannot fire even without this.)
* **A user logout** (`Logout`/`Exit Game` to character select or the login form) is ignored because
  the bot is stopped at that point: the logout trigger requires `TreeRoot.IsRunning`. The
  `ProcessExited` trigger still applies if the user closes the client while the bot is attached —
  that is the documented "client exits" case.
* The **glue string must be non-empty**. An all-zero buffer (client still booting) is *not*
  treated as a detected screen.

---

## 4. Retry policy and failure handling

* Retry budget: `MaxRetryCount` attempts per crash (`default 3`), `RetryDelaySeconds` between them
  (`default 30`). Attempt N of M is logged.
* **Fatal reasons stop immediately** (no point retrying): invalid/missing `WowExecutablePath`,
  credentials rejected by the server, relogin not configured, client stuck in its patch updater.
* **Retryable reasons**: client exited during startup/login (crash loop), login screen never
  became readable, in-world timeout, character not selectable, server unreachable.
* **On giving up**, one red line states the last failure *and the step it reached*, e.g.

  ```
  [Relogin] Attempt 3/3 failed at step LoggingIn: the server did not answer the login attempt (server unreachable or realm offline).
  [Relogin] Giving up after 3 attempt(s), maximum retries exhausted. Last failure — step LoggingIn: ...
  ```

  `_gaveUp` latches: the monitor keeps watching (it will notice a *new* crash) but does not
  re-enter a sequence until the user presses Start again (`ReloginManager.OnBotStarted` re-arms
  the budget) or saves the settings.

### Wrong password vs. unreachable server

Both end up back on the `login` screen and the client exposes **no explicit rejection flag**
(there is no glue-error offset in this codebase), so two independent observations are required
before calling it a credential problem:

1. the client **never** entered the loading/connecting state (`IsLoadingOrConnecting`) after the
   credentials were submitted — authentication never got far enough to reach the world server, and
2. it came back within `LoginRejectSeconds`.

Only then is it reported as **"the server refused the login …"** and treated as **fatal**, so the
same wrong password is not retried in a loop. If the client *did* connect (the flag went up) the
attempt is reported as a connection failure and stays retryable, and a slow bounce past
`LoginRejectSeconds` is likewise retried. This means an incorrect password on a reachable server
is classified correctly in both the fast-reject and the connected-then-dropped cases.

---

## 5. Relaunch / relogin sequence

`ReloginManager.RunAttempt` — one attempt:

1. **Client process.** If none exists: validate `WowExecutablePath`, `Process.Start` with the
   working directory set to the executable's folder, wait (bounded) for input idle.
   If the trigger was `ClientHung`, the frozen process is closed first.
2. **Attach + hook.** `ReloginClient.TryAttachAndHook(pid)`:
   * releases a stale attachment if it belongs to a *different* pid (the crashed client),
   * `new Memory(pid)` → `ObjectManager.Initialize(memory)` → EndScene hook,
   * `StyxWoW.Reset()` invalidates every static that cached an address inside the previous client
     (`WoWDb` DBC table pointers, `WoWCache` entries); without it, DBC lookups after a relaunch
     read unmapped memory.
   The loop retries the hook until the D3D device exists (normal right after launch), bounded by
   `LoginScreenTimeoutSeconds`.
3. **Credential/realm/character loop** (single loop, driven by the glue screen each poll):
   * `login` → `DefaultServerLogin`; a *second* arrival at `login` is classified per §4,
   * `realmwizard`/`movie`/`credits`/`trialconvert` → realm list chunk / dismiss,
   * `charcreate` → `CharacterCreate_Back()`,
   * `charselect` → `CharacterSelect_SelectCharacter(i)` + `EnterWorld()`, or `RequestRealmList(1)`
     if the configured realm is not the current one. Consecutive realm-switch requests are
     counted; after `MaxRealmSwitches` (5) the attempt fails with "the client could not switch to
     realm …" instead of spinning until the screen timeout,
   * `patchdownload` → fatal.
4. **In-world confirmation** (`ConfirmInWorld`): `g_ClientConnectionState != 0` **and**
   `ObjectManager.Me != null`. `EnterWorld` is re-issued at 15 s / 30 s / 45 s in case the first
   call raced the character-list refresh, then the attempt fails at `InWorldTimeoutSeconds`.
5. **Resume** — only after step 4 returns true. `ReloginManager.ResumeBot` is raised, and
   `MainWindow.ReloginManager_ResumeBot` (on the UI thread):
   * adopts the new pid, re-points the hotkeys at the new window
     (`HotkeysManager.UpdateWindowHandle` — `Initialize` is one-shot),
   * waits (bounded 30 s) until `StyxWoW.IsInWorld` and the object manager are usable, yielding to
     the dispatcher between polls; if it expires the bot is left **stopped** and the failure is
     reported rather than starting anyway,
   * `LoadSettings()` (re-creates per-character settings for the character),
   * reloads `LevelbotSettings.LastUsedPath` / `CharacterSettings.LastUsedPath` via
     `ProfileManager.LoadNew`,
   * waits for the old `TreeRoot` worker to be fully stopped, then calls the existing `StartBot()`
     — i.e. the same BotBase through the normal start path. No parallel start mechanism.

Per-attempt log line format:

```
[Relogin] Relogin triggered: ProcessExited — client process is gone.
[Relogin] Attempt 1/3 (trigger: ProcessExited).
[Relogin] Starting client: C:\WoW\Wow.exe
[Relogin] Client started, pid 8124.
[Relogin] Attached to pid 8124 and hooked EndScene.
[Relogin] Logging in as myaccount (glue: Login).
[Relogin] At character selection, selecting 'Mytoon'.
[Relogin] Character 'Mytoon' selected; EnterWorld issued.
[Relogin] In the world as Mytoon (level 80 BloodElf Mage).
[Relogin] Attempt 1/3 succeeded: character is back in the world.
[Relogin] Resumed profile: 1-80 Questing.xml
[Relogin] Resuming botbase: Questing
```

---

## 6. Files

**New**

| File | Purpose |
| --- | --- |
| `Styx/Helpers/ReloginSettings.cs` | settings class + `Settings/ReloginSettings.xml` |
| `Styx/Logic/Relogin/ReloginManager.cs` | detection thread, retry policy, sequence, `ResumeBot` event |
| `Styx/Logic/Relogin/ReloginClient.cs` | process/memory helpers, glue probe, login/charselect Lua |
| `docs/AutoRelogin.md` | this document |

**Modified** (all additive; no public API renamed)

| File | Change |
| --- | --- |
| `Styx/Offsets/GlobalOffsets.cs` | `GlueScreenString = 0x00B6A9E0` |
| `Styx/StyxWoW.cs` | `Reset()` also invalidates `WoWDb` / `WoWCache` client-address caches |
| `Styx/WoWInternals/WoWDb.cs` | `internal static ResetForNewProcess()` |
| `Styx/WoWInternals/WoWCache/WoWCache.cs` | `internal ResetForNewProcess()` + `BuildCaches()` extracted from the ctor |
| `Styx/Common/HotkeysManager.cs` | `UpdateWindowHandle(IntPtr)` (additive) |
| `UI/SettingsWindow.xaml` | "Auto-Relogin" cards (toggle, credentials, path, retry) |
| `UI/SettingsWindow.xaml.cs` | bind `ReloginSettings`, password round-trip, browse button, apply on save |
| `UI/MainWindow.xaml.cs` | start monitor after attach, resume handler, stop monitor on close, re-arm on Start |
| `Styx/Localization/Strings.resx` | 9 new UI strings |

### Behaviour when disabled (the no-change guarantee)

`ReloginManager.Start` is still called on attach and on settings save, but with `Enabled == false` it
returns after retiring any previous monitor: **no thread, no timer, no polling, no memory probe**.
`ReloginManager_ResumeBot` is never raised, `OnBotStarted` only resets two integers, and the
settings file is a new file that no other code reads. The only unconditional additions on existing
paths are: one extra `TryX`/`Reset`-style call in `StyxWoW.Reset()` (only reached from relogin) and
the new additive `HotkeysManager.UpdateWindowHandle` method (never called unless a relogin
succeeds).

---

## 7. Test plan

Prerequisites unless stated: `Enabled = true`, valid account/password/realm/character, valid
`WowExecutablePath`, defaults (`30s`, `3`, `120s`, `180s`, `45s`, `20s`). Watch the log panel and
`Logs/*.log`.

### T1 — Normal crash, successful relogin

1. Attach, load a profile, select e.g. Questing, press **Start**, let the bot run for a minute.
2. Kill the client: Task Manager → *End task* on `Wow.exe` (or `taskkill /f /im Wow.exe`).
3. Expect within ~1 s: `Relogin triggered: ProcessExited — client process is gone`,
   `Attempt 1/3`, `Starting client`, `Client started, pid …`, `Attached … and hooked EndScene`,
   `Logging in as …`, `At character selection`, `Character '…' selected`, `In the world as …`,
   `succeeded`.
4. Then: `Resumed profile: <name>` and `Resuming botbase: <name>`; the UI shows Running and the bot
   continues on the same profile (check the status text / Info panel XP counters climbing again).
5. Assert: the bot did **not** resume before the `In the world` line, and the same profile name
   appears in both the pre-crash and post-resume "Profile loaded" lines.

### T2 — Server down, retry exhaustion

1. Fully in game and running; stop the server (or block the login realm port).
2. Kill the client.
3. Expect: relaunch, then repeated `Logging in as …` with no `/` character selection.
4. With defaults, after attempt 1 fails at ~120 s: `Waiting 30s before attempt 2/3`, attempt 2,
   attempt 3, then:
   `Giving up after 3 attempt(s), maximum retries exhausted. Last failure — step WaitingForCharacterSelect: the server did not answer the login attempt (server unreachable or realm offline).`
5. Assert: no further attempts (monitor stays quiet for 5+ minutes), bot not started, reason
   visible in the log. Pressing **Start** manually re-arms the budget (logged).

### T3 — Wrong password

1. Set `Password = wrongpassword`, keep everything else valid.
2. Kill the client (or log the character out to the login screen while the bot is running).
3. Expect: relaunch, `Logging in as …`, then
   `Attempt 1/3 failed at …: credentials rejected by the server (wrong account name or password)`
   followed by `Not retrying this attempt: …` — exactly **one** attempt, no retry loop.

### T4 — Option disabled (no behaviour change)

1. Uncheck "Auto-relogin on crash", Save. Log shows `Auto-relogin is disabled.` at Debug level.
2. Kill the client. Expect the pre-existing behaviour only:
   `WoW process has exited. Stopping bot.` / `Bot stopping! Reason: WoW process exited` — **no
   `[Relogin]` lines**, no relaunch, no login, the client stays closed.
3. Log the character out normally (bot stopped), then back in: nothing happens.
4. Close CopilotBuddy: no delay and no `[Relogin] Monitor stopped` line (no thread was created —
   verifiable in a debugger/Process Explorer thread list: no "Auto-Relogin Monitor").

### T5 — Extra checks

* **Brief lag does not trigger.** While running, induce a 5–15 s client stall (alt-tab into a
  heavy load, or suspend the process with Process Explorer for 10 s below the grace threshold).
  Expect `Client window is not responding; watching for 45s …` followed by `Client window
  responding again after Ns; no relogin needed.` — no attempt.
* **User logout while stopped does not trigger.** Stop the bot, log out to character select,
  wait 2 minutes: expect at most a Debug line
  `Client left the world while the bot is stopped; not a crash, so no relogin.`
* **Invalid executable path.** Rename `Wow.exe`, kill the client: single attempt, fatal
  `WoW executable path is invalid: …`, no retries.
* **Missing configuration.** Enable with an empty password: red line on Save naming the missing
  field; a crash then fails safely with the same reason.
