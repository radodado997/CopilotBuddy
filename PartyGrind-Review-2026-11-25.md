# PartyGrind review — 2026-11-25

Branch: `arena/e5448586-copilotbuddy` (base `361612f`).
Scope: the three regressions reported by the user (dungeon pulls, heal bridge, buff loops) plus the
evidence visible in the five `2026-11-25_1414_*.log` client logs.

## What the logs actually show

All five clients entered **Stormwind Stockade** (`dungeonId=12`, map 34) at ~14:14:38 and wiped or
nearly wiped in the first cell block, ~35 seconds later:

| time | client | event |
|---|---|---|
| 14:14:42.363 | Bedogie (Prot Paladin, tank) | `Dungeon: advancing toward Targorr the Dread` |
| 14:14:43.838 | Bedogie | `[PartyGrind Commit] Started … (10s)` — pull of `Defias Inmate` |
| 14:14:44.185 | Ksionc (Holy Priest) | `Healer assisting attack` → `Smite` (984) at 100 % mana |
| 14:14:44–14:15:16 | Bedogie | one uninterrupted fight against **≥ 6 mobs** (`Defias Inmate`, `Prisoner`, `Captive`) |
| 14:15:12.059 | Ksionc | `player died` — healed the tank, shielded itself at 18 % HP, died |
| 14:15:16.186/504 | Bedogie | `Pull routine: Defias Prisoner` → `player died` |
| 14:15:18 | Bedoes (Warlock) | 18.8 % HP, `Heal: no spell for Bedoes (Warlock)` |

Targorr the Dread is the first boss of the Stockade and he stands in the middle of the prison-cell
room. The tank was not walking a cleared corridor: he was walking **at the boss**, and the whole
room came with him. That is the first reported bug, and the log line proves it.

## 1. Dungeon pulls — the tank charged the boss through the trash

### Root causes

1. `SquadDungeonService.ComputeLeaderNextDungeonPoint()` returned a raw destination point, and the
   bot moved there directly:
   * `AdvanceBossBreadcrumb()` locks onto the live boss as soon as the *path* distance is ≤ 25 yd
     (`return liveBossLocation`), and returns the live boss location when the profile breadcrumbs are
     exhausted — there is no notion of "the corridor between me and the boss is still dirty".
   * The `fallbackBoss` query accepted **any** live profile boss within **100 yd** of path distance
     and returned its raw location. It bypasses the breadcrumb corridor completely and is exactly the
     "moving toward the closest boss" behaviour the user described.
2. Nothing in `PartyGrindBot.RunTravelOrPullBehavior()` checked whether live trash sat between the
   tank and that destination, so every pack on the way was body-pulled. Pack social aggro then adds
   the neighbours of the mob that was pulled.
3. The pull opener was class-static: a Paladin always opened with `Avenger's Shield` first
   (`SquadTankBridge.TryExecuteSafeDungeonPull`). Avenger's Shield bounces to **three** targets, so
   even a "controlled" pull inflated by up to two extra mobs when the target stood inside its pack.

### Fixes

* `SquadDungeonService.ResolveDungeonAdvanceStep(from, destination, hostiles, maxStep, clearance)` —
  new pure method. Every advance is clamped to `DungeonAdvanceStepDistance` (18 yd) so the tank can
  never charge a distant boss, and the step is refused (`WoWPoint.Zero`) while a live hostile sits
  within `DungeonAdvanceTrashClearance` (12 yd) of the corridor segment (2D distance, ±8 yd height
  tolerance so mobs on the floor above do not block). This is the "breadcrumbs + clear the path
  before the boss" rule.
* `PartyGrindBot` — `CollectDungeonTrashPoints()` feeds the gate with live, attackable hostiles in a
  40 yd radius; when the step is blocked the tank stops, logs
  `Dungeon: clearing trash before <boss>` once, and lets the pull machinery kill the pack in front.
  A `DungeonAdvanceBlockMaxMs` (8 s) escape keeps an unreachable pack (no LoS, uneven floor) from
  wedging the run forever.
* `BossFallbackApproachDistance` (40 yd) replaces the hard-coded 100 yd boss beeline.
* `SquadTankBridge.GetPullSpellPriority(class, isolatedTarget)` — an isolated target may still be
  opened with Avenger's Shield, a packed target is opened with a single-target spell
  (`Hand of Reckoning` → `Exorcism` → `Avenger's Shield`) so the pull stays one pack.

## 2. Heal bridge — the healer is casting all the time

### Root causes

1. `SquadHealBridge.ComputeEffectiveHealThreshold()` returned **90 % during combat for every target**
   and **78 % out of combat**. Out of combat that is *above* the rest trigger
   (`TriggerRestHealthPct = 50`), so the healer healed the party instead of letting food/drink
   regenerate it, and never stopped casting.
2. The threshold ignored the healer's own mana. A healer at 20 % mana kept topping everyone up.
3. The routine hand-off was unconditional: on every tick with any candidate below the threshold the
   bridge ticked `RoutineManager.Current.HealBehavior`, which has its **own** targeting and happily
   heals 80–90 % members. The squad's mana policy was therefore bypassed entirely.
4. `TryHealerAssistDps()` allowed Smite filler from 70 % mana (visible at 14:14:44 in the log) —
   burning the reserve needed for the next pull.
5. Spell order: Paladin picked `Holy Shock` from 70 % HP (expensive instant on a scratch), and the
   Priest preferred `Power Word: Shield` down to 35 % HP — which is why Ksionc shielded itself at
   18 % and died 1.8 s later instead of casting `Flash Heal`.

### Fixes

* New thresholds: combat **75 %**, out of combat / resting **40 %**, interrupt an active drink only
  below **35 %**; hybrid emergency 50 % (combat) / 40 % (OOC); main healer down 70 % / 55 %.
* `ApplyManaReserveThreshold()` caps the threshold by the healer's mana
  (< 15 % → 35, < 30 % → 50, < 45 % → 60); it is applied in both `HasHealCandidate()` (the gate) and
  `RunHealerTick()` so the two can never disagree.
* The routine `HealBehavior` hand-off is now limited to **combat** and to targets below 70 %; outside
  combat the bridge's own efficient, class-aware spell selection is used.
* `TryHealerAssistDps()` requires `max(HealerDpsAboveManaPct, 80)` % mana.
* Priest: `Flash Heal`/`Greater Heal` first below 35 %/25 %. Paladin: `Holy Shock` only below
  50 %, `Flash of Light` below 78 %, `Holy Light` below 55 %.

## 3. Buff loop — BoK/BoM ping-pong

### Root cause

`SquadPartyBuffService.BuffPaladin()` gave **every** Paladin its own blessing per target:

* Paladin index 0 → `Blessing of Sanctuary` on the tank, `Blessing of Might` on melee,
  `Blessing of Wisdom` on casters;
* Paladin index 1 → `Blessing of Kings` on essentially everyone (`SelectBlessingForRole`).

Paladin blessings share **one buff slot per player**, so Paladin 1 kept overwriting Paladin 0's
blessing, each of them then saw "its" blessing missing and recast it — an endless
Kings → Sanctuary → Might → Kings loop, exactly like `Plaa` throwing BoK/BoM non-stop (and it also
blocked the buff tick from ever finishing, delaying every subsequent behaviour).

Two aggravating bugs:

* `paladinIndex = Math.Max(0, paladinNames.FindIndex(me))` — when the leader roster did not contain
  the local player (telemetry filtering / stale snapshot) **every** Paladin resolved to index 0, so
  two Paladins used the same assignments instead of complementary ones.
* `IsNonHolyPaladinAuraOwner()` had the mirrored problem: when the roster did not contain the local
  player and the party had another non-holy Paladin, **each** of them saw the other as the owner and
  concluded "not me" → nobody managed the aura at all.

### Fixes

* `SelectBlessingAssignment(role, class, knownByPaladin)` — deterministic, group-wide assignment:
  one blessing per target and exactly one Paladin owning it. The owner is the first Paladin in the
  roster that actually knows the preferred blessing (squad telemetry `KnownBlessings` is used for the
  other Paladins, `SpellManager.HasSpell` for self).
* `HasAnyPaladinBlessing()` — a target that already carries **any** blessing (normal or Greater) is
  never re-buffed, so the slot can never bounce between Paladins even with an inconsistent roster.
* `BuildPaladinRoster()` always contains the local player; `IsNonHolyPaladinAuraOwner()` computes the
  owner from a roster that includes self (tank first, then name).

## Tests

* `Tools/CopilotBuddy.PartyGrind.Tests/DungeonPullAndManaGuardTests.cs` (new): breadcrumb step
  clamping, trash-gated corridor, height/side tolerance, forced advance, corridor distance maths,
  mana reserve table, blessing assignment (incl. fallback to the Paladin that knows the spell and the
  "no owner" case), blessing slot lock, pull-opener priority.
* `SquadFormationAndRolesTests.HealBridge_ConservesManaOutOfCombatAndPreservesActiveDrinking`
  updated to the new thresholds (75 / 40 / 40 / 35).

## Not fixed — worth a follow-up

These are visible in the same logs but were left alone deliberately (behavioural tuning without an
in-game test bench is risky):

* **Focus thrash**: `[PartyGrind Focus] A -> B -> A` every 2–4 s during the same fight
  (`CombatFocusStickyMs = 2500`). The tank and the DPS keep splitting damage across the pack.
* **Pull commit released 0.3 s after start** (`Commit Started` 14:14:43.838 → `Released` 14:14:44.739):
  the commit only covers the cast, not the fight, so it does not stabilise focus.
* `Warning: QueryPolygons failed while applying blackspot … tile not loaded?` — profile blackspots are
  applied before the dungeon tiles are streamed in.
* `[Relogin]` prints `Not in game` in a tight loop while the client is out of the world.
* Corpse run resolved the Stockade entrance as `(-8763,847,87)` and started a 888 yd approach —
  suspicious entrance source for this dungeon (`source=death-script`).
* `Heal: no spell for Bedoes (Warlock)` — a `HybridOffHeal` without a heal spell has no fallback
  (healthstone/`Drain Life`); only noisy, but it hides real "out of mana" situations.

## Verification status

The sandbox has no .NET SDK / Windows and the project targets `net10.0-windows7.0` with WPF, so
`dotnet build` / `dotnet test` could not be executed here. The changes are pure-logic additions next
to existing patterns plus threshold constants; the behavioural parts (dungeon movement, pull, heal,
buff) still require the mandatory in-game test with a log per `CONTRIBUTING.md`.
