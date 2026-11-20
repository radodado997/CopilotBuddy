# Plan implementacji napraw zapętleń PartyGrind — dla agenta AI

**Branch:** `arena/e3d71216-copilotbuddy` (commit bazowy `2effa4da`)  
**Data:** 2026-11-20  
**Poprzedni artefakt:** `DIAGNOZA_PLAN.md` (pełna diagnoza z cytatami z logów) — ten plan jest jej bezpośrednim rozwinięciem wykonawczym.  
**Cel:** wyeliminowanie dwóch zapętleń bez przebudowy całej logiki bota — zmiany minimalne, testowalne, z weryfikacją w logach.

---

## 1. Kontekst wykonawczy i pliki do modyfikacji

### Drzewo zachowań (`PartyGrindBot.BuildRootBehavior`)

Priorytet malejąco (pierwszy `Success` wygrywa):

1. Paused / disconnected
2. Dead / CorpseRun (`RunDeathAndCorpseBehavior`)
3. TownRun (`SquadTownRunService.RunTownTick`)
4. **Combat & Role Bridges** → `RunCombatAndRoleBehavior()` — tu leży Problem 1
5. PostCombatRes `SquadHealBridge.RunPostCombatResurrectionTick`
6. Rest
7. Provisioning
8. Buffs
9. Looting (`FindEligibleLoot` + `Navigator.MoveTo(loot)`)
10. **Travel / Pull / Roam** → `RunTravelOrPullBehavior()` — tu leży Problem 2

Reguła: każdy tick startuje od góry. Jeśli decorator 4 zwróci `Success`, decorator 10 **nie jest** wykonywany w tym tiku.

### Kluczowe pliki

| Plik | Rola | Linie krytyczne |
|------|------|-----------------|
| `Bots/PartyGrind/PartyGrindBot.cs` | Główny BotBase, `Root`, `Pulse`, `UpdateCoordinatorTargets`, `RunDpsBehavior`, `PositionBackline`, `RunCombatAndRoleBehavior`, `RunTravelOrPullBehavior` | `~560-600` `UpdateCoordinatorTargets`, `~680-750` `RunCombatAndRoleBehavior`, `~760-820` `RunDpsBehavior`, `~830-860` `PositionBackline`, `~1050-1150` `RunTravelOrPullBehavior` |
| `Bots/PartyGrind/Logic/SquadFormationService.cs` | `ComputeStandoffPosition`, `ComputeMeleeFlankPosition`, `ResolveSubRoleSlot` | `~35-85` |
| `Bots/PartyGrind/Logic/SquadCoordinatorService.cs` | `EvaluateTick`, `IsWaitingForFollower`, `IsHardBacktracking`, `CanInitiatePull` via `SquadTankBridge` | `~90-250` |
| `Bots/PartyGrind/Logic/SquadTankBridge.cs` | `CanInitiatePull`, `IsAllowedDpsTarget`, `ComputeTankTargetBonus` | `~40-90` |
| `Bots/PartyGrind/Logic/SquadHealBridge.cs` | `RunHealerTick`, `TryHealerAssistDps` | `~60-180` |
| `Tools/CopilotBuddy.PartyGrind.Tests/*.cs` | Testy xUnit — istniejące pokrycie formacji, koordynatora, mount, heal | — |

### Stan przed zmianami — co potwierdzają logi

- **Problem 1:** Healer `Ksionc` (`1249_15728`) 199× `Moving to backline`, 0× `Combat routine`; DPS `Bedoes` (`1006_1668`) przeplot `Combat → Backline` w 0.37s. `MeshNavigator Skipped` w każdym przeplocie.
- **Problem 2:** Tank `Bedogie` (`1006_5564`) 46× `Pull routine: Prairie Stalker` / 100× `Moving to grind hotspot` / 147× `Skipped`; `1249_20932` 50×/170×/169× — ten sam mob wybierany 5× w 15s.

---

## 2. Problem 1 — DPS/Healer zapętlają backline

### 2.1. Cel funkcjonalny

DPS/Healer ma **dokończyć walkę/leczenie** zanim wróci na pozycję tyłową. Pozycja tyłowa korygowana tylko gdy: poza zasięgiem/LoS, po śmierci celu, lub po zakończeniu walki. Brak oscylacji `Combat ↔ Backline` <5s.

### 2.2. Przyczyna w kodzie (skrót)

```csharp
// PartyGrindBot.cs — RunDpsBehavior (isMelee=false)
if (PositionBackline(standoff, leader, me, focus)) return Success; // linia ~790
return RunRoutineCombat(focus.Guid); // linia ~795 — nigdy nie osiągnięte gdy PositionBackline true

// PositionBackline
slotPos = SquadFormationService.ComputeStandoffPosition(tankPos, facing, focusPos, standoff, idx, cnt);
if (me.Location.Distance2D(slotPos) > 3.0f) { Navigator.MoveTo(slotPos); return true; }
return false; // COMBAT dopiero gdy false
```

- Próg wejścia == progu wyjścia (`>3.0`), brak histerezy.
- `slotPos` przeliczany co tick z `tankPos` (IPC 100ms) + `facing` (obrót tanka) + `focusPos` (ruch moba) → dryf celu.
- `PositionBackline` ma **wyższy priorytet** niż `RunRoutineCombat`, mimo że ranged może atakować z 30 yd.
- Brak sprawdzenia `IsCasting` / `_activeCombatRoutine == Running` → przerywa cast.

### 2.3. Zadania implementacyjne

#### TASK 1.1 — Dodać stan histerezy dla backline w `PartyGrindBot.cs`

**Plik:** `Bots/PartyGrind/PartyGrindBot.cs`

1. Dodać pola prywatne w sekcji pól bota (obok `_currentFocusGuid`, `_pullStartedUtcMs`):
   ```csharp
   private WoWPoint _latchedBacklinePos;
   private ulong _latchedBacklineTargetGuid;
   private long _latchedBacklineUntilMs;
   private long _reachedBacklineAtMs;
   // Opcjonalnie: bool _isMovingToBackline;
   ```
   - `_latchedBacklinePos` — zatrzaśnięta pozycja tyłowa na czas walki.
   - `_latchedBacklineTargetGuid` — guid moba dla którego policzono slot (unieważnia latch gdy guid się zmieni).
   - `_latchedBacklineUntilMs` — czas wygaśnięcia latcha (np. po śmierci celu lub timeout).
   - `_reachedBacklineAtMs` — moment osiągnięcia progu wyjścia (debounce 500ms).

2. Zdefiniować stałe (na górze klasy lub w `SquadFormationService`):
   ```csharp
   const float BacklineEnterDistance = 8.0f; // próg wejścia do marszu (było 3.0)
   const float BacklineExitDistance  = 3.0f; // próg uznania pozycji za osiągniętą
   const long  BacklineLatchMs       = 8000; // min. czas trzymania latcha (8s)
   const long  BacklineReachDebounceMs = 500; // stabilizacja w progu wyjścia
   ```

3. W `Stop()` i `OnPaused()` czyścić latch:
   ```csharp
   _latchedBacklinePos = WoWPoint.Zero;
   _latchedBacklineTargetGuid = 0;
   ```

**Kryterium:** kompilacja przechodzi, pola są zerowane przy starcie/pauzie.

#### TASK 1.2 — Przepisać `PositionBackline` na wersję z histerezą i latch-em

**Plik:** `Bots/PartyGrind/PartyGrindBot.cs`, metoda `PositionBackline(float standoffDistance, SquadLeaderState? leader, LocalPlayer me, WoWUnit? selectedTarget = null)`

**Kroki:**

1. Zmienić metodę z `static` na instancyjną (`private bool PositionBackline(...)`) aby miała dostęp do pól latcha. Jeśli musi zostać static, przenieść stan do `SquadFormationService` lub przekazać przez `SquadContext` — preferowane: instancyjna w `PartyGrindBot`.

2. Na wejściu dodać **guardy które blokują pozycjonowanie** (zwracają `false` → pozwól walce iść dalej):
   ```csharp
   // 1. Nie przerywaj casta / aktywnej rutyny
   if (me.IsCasting || me.ChanneledCastingSpellId != 0) return false;
   if (_activeCombatRoutine != null /* && is Running */) return false; // sprawdź flagę _combatTicked lub status
   // Dla Healer-a: if (SquadHealBridge.IsHealing) return false; — użyj istniejącego _healTicked lub nowej flagi

   // 2. Jeśli w walce i cel w zasięgu + LoS → nie idź do tyłu
   if (selectedTarget != null && selectedTarget.IsValid && !selectedTarget.Dead) {
       if ((me.Combat || selectedTarget.Combat || selectedTarget.IsTargetingMyPartyMember)
           && selectedTarget.Distance <= 30f && selectedTarget.InLineOfSpellSight) {
           return false;
       }
       // Alternatywnie: dla Healer-a sprawdź najbliższy heal target zamiast selectedTarget
   }
   // 3. OOC vs InCombat różnicowanie:
   // Jeśli !me.Combat && leader.InCombat && selectedTarget != null → dozwól JEDEN marsz na początku pulla,
   // ale potem gdy me.Combat==true, latch blokuje dalsze korekty.
   ```

3. Zaimplementować **latch**:
   ```csharp
   // Wyznacz guid dla latcha: selectedTarget?.Guid ?? leader.FocusTargetGuid
   ulong latchGuid = selectedTarget?.Guid ?? leader?.FocusTargetGuid ?? 0;
   long now = Environment.TickCount64; // lub DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

   // Unieważnij latch gdy guid się zmienił lub cel martwy lub timeout
   if (latchGuid != _latchedBacklineTargetGuid) {
       _latchedBacklinePos = WoWPoint.Zero;
       _latchedBacklineTargetGuid = latchGuid;
       _reachedBacklineAtMs = 0;
   }
   // Sprawdź czy latched pozycja nadal ważna: tank oddalił się >10 yd od miejsca latcha
   // lub leader.Facing zmienił się >30° i me jest już w walce → odśwież latch tylko OOC

   // Oblicz nowy slot tylko jeśli latch pusty lub wygasł
   WoWPoint slotPos;
   if (_latchedBacklinePos != WoWPoint.Zero && now < _latchedBacklineUntilMs) {
       slotPos = _latchedBacklinePos;
   } else {
       var (slotIdx, slotCount) = SquadFormationService.ResolveSubRoleSlot(me.Name, settings.Role, leader);
       slotPos = SquadFormationService.ComputeStandoffPosition(tankPos, leader.Facing, focusPos, standoffDistance, slotIdx, slotCount);
       // Zatrzaskuj tylko gdy wchodzimy do marszu lub gdy nie jesteśmy w walce
       // _latchedBacklinePos = slotPos; _latchedBacklineUntilMs = now + BacklineLatchMs;
   }

   // Histereza dwuprogowa
   float dist2D = me.Location.Distance2D(slotPos);
   if (dist2D > BacklineEnterDistance) {
       // Wejście do marszu — zatrzaśnij pozycję
       if (_latchedBacklinePos == WoWPoint.Zero) {
           _latchedBacklinePos = slotPos;
           _latchedBacklineUntilMs = now + BacklineLatchMs;
       }
       SquadPartySyncService.LogAction("Moving to backline position");
       Navigator.MoveTo(_latchedBacklinePos, "Holding backline standoff");
       _reachedBacklineAtMs = 0;
       return true;
   }
   if (dist2D < BacklineExitDistance) {
       // Wyjście — debounce 500ms
       if (_reachedBacklineAtMs == 0) _reachedBacklineAtMs = now;
       if (now - _reachedBacklineAtMs >= BacklineReachDebounceMs) {
           // Uznaj pozycję za osiągniętą, ale trzymaj latch do końca walki
           return false;
       }
       // Nadal w debounce — kontynuuj marsz aby ustabilizować
       Navigator.MoveTo(_latchedBacklinePos, "Holding backline standoff");
       return true;
   }
   // Strefa histerezy (3-8 yd): trzymaj poprzednią decyzję
   // Jeśli wcześniej szliśmy → idź dalej; jeśli staliśmy → stój
   // Można zaimplementować jako: if (_reachedBacklineAtMs !=0) return false else return previousDecision
   // Najprościej: w histerezie nie ruszaj się jeśli nie byłeś w marszu
   return false; // lub sprawdź flagę _isMovingToBackline
   ```

4. **Ważne:** `Distance2D` vs `Distance` — rozważyć użycie `Distance` (3D) lub `Distance2D` + korekta Z (np. `Math.Abs(me.Z - slotPos.Z) < 5`). Na wzgórzach Mulgore `Distance2D` zaniża dystans.

**Test manualny:** Healer `Ksionc` nie powinien logować `Moving to backline` częściej niż 1× na pull.

#### TASK 1.3 — Odwrócić priorytet w `RunDpsBehavior` i `RunCombatAndRoleBehavior` (Healer)

**Plik:** `Bots/PartyGrind/PartyGrindBot.cs`

1. W `RunDpsBehavior`:
   ```csharp
   // PRZED: if (PositionBackline(...)) return Success;
   // PO: 
   // 1. Sprawdź czy cel w zasięgu/LoS i me.Combat → walcz
   // 2. Dopiero jeśli poza zasięgiem/LoS lub OOC → PositionBackline
   // 3. Inaczej → RunRoutineCombat
   ```
   Konkretnie:
   ```csharp
   // a) Sprawdź combat-readiness (zachowaj istniejące checki Aggro Peel i DpsEngageDelay)
   // b) Wyznacz focus (jak obecnie)
   // c) Jeśli focus != null:
   //    if (focus.Distance <= 30f && focus.InLineOfSpellSight && (me.Combat || focus.Combat)) {
   //        // Jesteś w walce i możesz bić — nie pozycjonuj
   //        return RunRoutineCombat(focus.Guid);
   //    }
   //    if (PositionBackline(...)) return Success;
   //    return RunRoutineCombat(focus.Guid);
   ```

2. W `RunCombatAndRoleBehavior` gałąź `Healer`:
   ```csharp
   case SquadRole.Healer:
       if (SquadHealBridge.RunHealerTick(...)) { _healTicked=true; return Success; }
       // Dodaj guard jak w 1.2: jeśli heal target w zasięgu 35yd + LoS i me.Combat → nie backline
       // Sprawdź czy najbliższy heal candidate jest w zasięgu 35yd — jeśli tak, nie pozycjonuj
       if (PositionBackline(...)) return Success;
       if (SquadHealBridge.TryHealerAssistDps(...)) return Success;
       return Success;
   ```
   Analogicznie dla `HybridOffHeal` który woła `RunDpsBehavior(isMelee:false)` — tam priorytet walka>pozycja już będzie.

3. Dodać czyszczenie latcha gdy walka się kończy:
   ```csharp
   // W Pulse() lub w RunDpsBehavior gdy focus==null (brak celu) i !me.Combat:
   // _latchedBacklinePos = WoWPoint.Zero; _latchedBacklineTargetGuid=0;
   ```

**Kryterium:** DPS logi pokazują ciągły blok `Combat routine` 5-15s bez `Moving to backline` w środku.

#### TASK 1.4 — (Opcjonalnie) Dostosować `SquadFormationService.ComputeStandoffPosition`

Jeśli TASK 1.2 wymaga stabilniejszego slotu, dodać parametr `bool useLatchedPos` lub nową metodę `TryGetLatchedStandoff` — ale preferowane trzymać logikę latcha w `PartyGrindBot`, a `ComputeStandoffPosition` zostawić bez zmian (jest czysta matematyka, testy jej nie ruszają).

Jeśli jednak potrzebna zmiana, to:
- Dodać overload `ComputeStandoffPosition(..., WoWPoint? latchedPos)` który zwraca latched gdy nie null.
- Zaktualizować test `FormationMath_MeleeAndBacklineSlotsNeverOverlap` — testy nie powinny się zepsuć (są deterministyczne).

#### TASK 1.5 — Testy

1. **Unit testy nowe** w `Tools/CopilotBuddy.PartyGrind.Tests/`:
   - `BacklineHysteresis_EnterAt8ExitAt3` — symuluj pozycje me/tank/focus, sprawdź że `PositionBackline` zwraca `true` dopiero >8, a `false` <3 z debounce.
   - `Backline_LatchedDuringCombat` — ustaw `_latchedBacklinePos`, zmień `tankPos` o 2 yd w trakcie walki, sprawdź że `slotPos` nie dryfuje.
   - `Dps_PrefersCombatWhenInRangeAndLoS` — mock `me.Combat=true`, `focus.Distance=25`, `InLineOfSpellSight=true` → `RunDpsBehavior` nie woła `MoveTo(backline)`.

2. **Uruchomić istniejące:** `dotnet test ...SquadFormationAndRolesTests.cs` — wszystkie muszą przejść (zwłaszcza `FormationMath_*`, `ResolveSubRoleSlot_*`).

3. **Weryfikacja logów (jak w DIAGNOZA_PLAN.md):**
   - `grep -c "Moving to backline"` przed >99, po <20 na 30min walki.
   - `grep -c "Skipped.*path nodes"` między `Combat` a `Backline` → 0.
   - Brak `Combat → Backline` delta <2s.

---

## 3. Problem 2 — Tank oscyluje Pull / Roam

### 3.1. Cel funkcjonalny

Tank kończy rozpoczęty pull (dojście do moba i rozpoczęcie walki) zanim przejdzie do roam (`Moving to grind hotspot`). Brak oscylacji `Pull routine: Prairie Stalker → Moving to grind hotspot → Pull routine: Prairie Stalker` na tym samym mobie <3s. Spadek `Skipped` >70%.

### 3.2. Przyczyna w kodzie (skrót)

```csharp
// PartyGrindBot.cs — UpdateCoordinatorTargets (PulseCoordinator, co 100ms)
bool localCannotPull = dead||ghost||InCombat||NeedsRest||IsLooting;
if (newFocusGuid!=0 && !focus.Combat && !focus.IsTargetingMyPartyMember
    && (localCannotPull || Phase!=Grinding || IsWaitingForFollower || !CanInitiatePull)) 
    newFocusGuid=0; // zeruje guid
_currentFocusGuid = newFocusGuid;

// PartyGrindBot.cs — RunTravelOrPullBehavior (priorytet 10, fallback)
if (CanInitiatePull) { _currentFocusGuid=FirstUnit.Guid; RunRoutineCombat; }
if (grindArea.CurrentHotSpot... ) Navigator.MoveTo(hotspot); // Roam
```

- Dwa źródła `_currentFocusGuid` bez blokady → każdy tick może zmienić zdanie.
- `CanInitiatePull` zależy od `MaxPullGatherDistance` (np. 28 yd) i `IsWaitingForFollower` (SoftWait 35 yd). Tank idąc do moba oddala się → dystans > próg → `CanInitiatePull=false` → `newFocusGuid=0` → następny tick nie wchodzi do Combat (priorytet 4) tylko do Travel (10) → `MoveTo(hotspot)` nadpisuje `MoveTo(mob)` → `Skipped`.

### 3.3. Zadania implementacyjne

#### TASK 2.1 — Dodać blokadę PullCommit w `PartyGrindBot.cs`

**Plik:** `Bots/PartyGrind/PartyGrindBot.cs`, sekcja pól:

```csharp
private ulong _pullCommittedGuid;
private long _pullCommittedUntilMs;
private WoWPoint _pullCommittedPos; // pozycja moba w momencie commit
const long PullCommitTimeoutMs = 10000; // 10s (konfigurowalne via SquadSettings)
```

**Logika ustawiania commit:**

1. W `UpdateCoordinatorTargets`:
   ```csharp
   // Gdy ustawiamy nowy guid (newFocusGuid !=0 && newFocusGuid != _currentFocusGuid)
   if (newFocusGuid !=0 && newFocusGuid != _currentFocusGuid) {
       _pullStartedUtcMs = now;
       _pullCommittedGuid = newFocusGuid;
       _pullCommittedUntilMs = now + PullCommitTimeoutMs;
       _pullCommittedPos = focus.Location; // zapamiętaj pozycję
       _currentFocusGuid = newFocusGuid;
   } else if (newFocusGuid ==0) {
       // Sprawdź czy mamy aktywny commit — jeśli tak, NIE zeruj
       if (_pullCommittedGuid !=0 && now < _pullCommittedUntilMs) {
           // Sprawdź warunki zwolnienia commit (patrz niżej)
           var committedUnit = ObjectManager.GetObjectByGuid<WoWUnit>(_pullCommittedGuid);
           bool shouldRelease = 
               committedUnit==null || !committedUnit.IsValid || committedUnit.Dead || committedUnit.IsFriendly
               || me.Location.Distance(committedUnit.Location) > 60f
               || (me.Combat && committedUnit.Combat && me.CurrentTargetGuid==_pullCommittedGuid) // pull udany
               || committedUnit.HealthPercent < 100f && committedUnit.IsTargetingMyPartyMember;
           // Timeout już w warunku now < _pullCommittedUntilMs
           if (shouldRelease) {
               _pullCommittedGuid = 0;
               _pullCommittedUntilMs = 0;
               _currentFocusGuid = 0;
               _pullStartedUtcMs = 0;
           } else {
               // Utrzymaj stary guid mimo że CanInitiatePull false
               newFocusGuid = _pullCommittedGuid;
               _currentFocusGuid = newFocusGuid;
               return; // nie zeruj
           }
       } else {
           // Brak commitu — normalne zerowanie
           _pullCommittedGuid=0; _pullStartedUtcMs=0; _currentFocusGuid=0;
       }
   }
   // Analogicznie: gdy newFocusGuid !=0 i jest taki sam jak _currentFocusGuid → odśwież timeout tylko jeśli nie ma commitu?
   ```

2. W `RunTravelOrPullBehavior` (Coordinator branch):
   ```csharp
   // Na początku gałęzi IsCoordinator:
   // 1. Obsłuż PullCommit priorytetowo
   if (_pullCommittedGuid !=0) {
       var committed = ObjectManager.GetObjectByGuid<WoWUnit>(_pullCommittedGuid);
       if (committed !=null && committed.IsValid && !committed.Dead) {
           // Sprawdź warunki zwolnienia jak wyżej; jeśli nie zwolnione:
           // Kontynuuj pull nawet jeśli CanInitiatePull false
           long now = Environment.TickCount64;
           if (now < _pullCommittedUntilMs && !IsPullCommitReleased(committed)) {
               committed.Target();
               return RunRoutineCombat(_pullCommittedGuid); // kontynuuj
           } else {
               _pullCommittedGuid=0; // timeout lub cel martwy
           }
       } else {
           _pullCommittedGuid=0;
       }
   }
   // Dopiero potem:
   if (_coordinator.IsHardBacktracking) { ... Backtrack ... }
   if (_coordinator.IsWaitingForFollower && _pullCommittedGuid==0) { ... Wait ... } // Wait tylko gdy brak commitu
   if (SquadTankBridge.CanInitiatePull(...)) { // nowy pull
       var target = Targeting.Instance.FirstUnit;
       if (target !=null) {
           _currentFocusGuid = target.Guid;
           _pullCommittedGuid = target.Guid;
           _pullCommittedUntilMs = now + PullCommitTimeoutMs;
           target.Target();
           return RunRoutineCombat(target.Guid);
       }
   }
   // Roam
   if (grindArea.CurrentHotSpot ...) Navigator.MoveTo(hotspot);
   ```

3. **Ważne:** `_pullStartedUtcMs` już istnieje — użyć jej jako `now` dla `PullStartedUtcMs` w `SquadLeaderState` (leaderState.PullStartedUtcMs). Upewnić się że `SecondaryTargetGuid` nie koliduje z commitem (secondary wybierany tylko gdy `_currentFocusGuid` ustalony).

4. Dodać helper `IsPullCommitReleased(WoWUnit committed)` aby logika zwolnienia była w jednym miejscu (używana i w `UpdateCoordinatorTargets` i w `RunTravelOrPullBehavior`).

#### TASK 2.2 — Dodać histerezę czasową dla `IsWaitingForFollower` / `HardBacktrack` w `SquadCoordinatorService.cs`

**Plik:** `Bots/PartyGrind/Logic/SquadCoordinatorService.cs`

1. Dodać pola:
   ```csharp
   private long _waitingSinceMs;
   private long _notWaitingSinceMs;
   private bool _debouncedIsWaiting;
   private long _hardBacktrackSinceMs;
   private long _notHardBacktrackSinceMs;
   private bool _debouncedIsHardBacktrack;
   const long DistanceDebounceMs = 2000; // 2s
   ```

2. W `EvaluateTick` po obliczeniu `maxFollowerDist` i surowej logiki:
   ```csharp
   // Surowa logika jak obecnie:
   bool rawWaiting = maxFollowerDist > settings.SoftWaitDistance;
   bool rawResume = maxFollowerDist <= settings.ResumeDistance;
   // Debounce:
   if (rawWaiting && !_debouncedIsWaiting) {
       if (_waitingSinceMs==0) _waitingSinceMs=nowUtcMs;
       if (nowUtcMs - _waitingSinceMs >= DistanceDebounceMs) { _debouncedIsWaiting=true; _notWaitingSinceMs=0; }
   } else if (rawResume && _debouncedIsWaiting) {
       if (_notWaitingSinceMs==0) _notWaitingSinceMs=nowUtcMs;
       if (nowUtcMs - _notWaitingSinceMs >= DistanceDebounceMs) { _debouncedIsWaiting=false; _waitingSinceMs=0; }
   } else {
       // W strefie histerezy (między SoftWait a Resume) — nie zmieniaj debounced, resetuj timery
       _waitingSinceMs=0; _notWaitingSinceMs=0;
   }
   _isWaitingForFollower = _debouncedIsWaiting; // użyj debounced do decyzji pull
   // Analogicznie dla HardBacktrack
   ```

3. Alternatywnie prostsze: nie zmieniać `SquadCoordinatorService`, tylko w `PartyGrindBot.CanInitiatePull` dodać bufor `+15 yd` gdy `_pullCommittedGuid !=0`:
   ```csharp
   float effectiveMaxDist = _pullCommittedGuid!=0 ? settings.MaxPullGatherDistance + 15f : settings.MaxPullGatherDistance;
   ```

   **Zalecenie:** zrobić obie rzeczy — debounce w koordynatorze (globalne) + bufor w `PartyGrindBot` (lokalne dla tanka). Debounce eliminuje szum, bufor pozwala dokończyć pull.

4. Upewnić się że `GetBacktrackTarget` nie jest wołane gdy `_pullCommittedGuid !=0` (chyba że `HardBacktrack` i dystans >65 yd — wtedy backtrack ma wyższy priorytet niż pull? Wg planu: `PullCommit > HardBacktrack > Wait` — ale `HardBacktrack` może być ważniejszy gdy tank jest 70 yd od grupy, bo grozi rozjechaniem party. Do decyzji: `PullCommit` wygrywa nad `Wait`, ale `HardBacktrack` wygrywa nad `PullCommit` tylko gdy `maxFollowerDist > HardBacktrackDistance + 10`. Zaimplementować tę hierarchię w `RunTravelOrPullBehavior`.

#### TASK 2.3 — Synchronizacja `CanInitiatePull` między `UpdateCoordinatorTargets` a `RunTravelOrPullBehavior`

Upewnić się że obie metody patrzą na **te same** warunki i ten sam `SquadLeaderState` (snapshot z `PulseCoordinator`). Przekazać `_pullCommittedGuid` jako parametr do `CanInitiatePull` lub dodać overload:

```csharp
public static bool CanInitiatePull(SquadLeaderState? leader, float maxDist, ulong pullCommittedGuid = 0) {
    if (pullCommittedGuid !=0) return true; // lub check z buforem
    // ... istniejąca logika
}
```

W `UpdateCoordinatorTargets` wołać `CanInitiatePull(decision, MaxPullGather, _pullCommittedGuid)` aby nie zerować guida gdy commit aktywny.

#### TASK 2.4 — Obsługa Looting (zgodnie z poleceniem — tylko jeśli dane potwierdzają)

Sprawdzić czy `IsLootWindowActive` (z `SquadCoordinatorService`) prawidłowo blokuje pull. Obecnie `CanInitiatePull` zwraca `false` gdy `leader.IsLootWindowActive` — to poprawne i nie jest przyczyną pętli (loot występuje po walce, nie w środku pulla). Upewnić się że `local.IsLooting` nie jest używane do zerowania `FocusGuid` gdy commit aktywny (bo `localCannotPull` zawiera `IsLooting`). W TASK 2.1 już to obsłużono: gdy commit aktywny, ignoruj `localCannotPull`.

Dodać komentarz w kodzie wyjaśniający dlaczego `Looting` nie jest modyfikowane.

#### TASK 2.5 — Testy

1. **Unit testy** w `Tools/CopilotBuddy.PartyGrind.Tests/SquadCoordinatorAndProfileTests.cs`:
   - `PullCommit_HoldsWhenFollowerDriftsBeyondMaxDist` — ustaw `MaxPullGather=28`, `SoftWait=35`, `followerDist=30` → `CanInitiatePull` true, ustaw commit, przesuń followera na 40 yd, sprawdź że commit nadal trzyma guid, a nie zeruje.
   - `PullCommit_ReleasesOnTimeoutOrDead` — commit 10s, symuluj `now+11000` → guid zerowany, `Moving to grind hotspot` dozwolone.
   - `DebouncedWaiting_Requires2sStability` — follower 40 yd przez 1s → `IsWaiting` nadal false; przez 2.1s → true; wróć na 18 yd przez 1s → nadal true; przez 2.1s → false.
   - `Priority_PullCommitOverWait` — `IsWaiting=true` + commit aktywny → `RunTravelOrPullBehavior` nie wchodzi w `Waiting`, tylko kontynuuje pull.

2. **Uruchomić istniejące:** `dotnet test ... -c Release /p:Platform=x86` — wszystkie muszą przejść, zwłaszcza `PhaseMachine_EnforcesRestAndDistanceHysteresis_AndTownRunCompletionGate`.

3. **Weryfikacja logów:**
   - `grep "Pull routine: Prairie Stalker" | wc -l` — przed 46, po <15 na sesję (bo każdy stalker zabity raz, nie 5×).
   - `grep "Moving to grind hotspot"` — przed 100, po stabilnie ~30 (roam między walkami, nie w środku pulla).
   - `grep "Skipped.*path nodes" | wc -l` — przed 147, po <40.
   - Korelacja `Skipped` w odległości ≤2 linii od `Pull`/`hotspot` → 0 (skrypt Py z diagnozy).
   - Sekwencja `Pull → Combat` (bez `hotspot` pomiędzy) — przed 20%, po >80%.

---

## 4. Kolejność wdrożenia i zależności

```
Etap 1: TASK 1.1 + 1.2 (histereza backline) — niezależne, krytyczne
   ↓
Etap 2: TASK 1.3 (priorytet walka > pozycja) — zależy od 1.1
   ↓
Etap 3: TASK 2.1 (PullCommit) — niezależne od Etapu 1/2, ale warto po nich
   ↓
Etap 4: TASK 2.2 + 2.3 (debounce + synchronizacja CanInitiatePull)
   ↓
Etap 5: TASK 1.5 + 2.5 (testy + weryfikacja logów)
   ↓
Etap 6: Regresja (TownRun, CorpseRun, Looting, Provisioning)
```

**Dlaczego taka kolejność:** Problem 1 jest poważny (blokuje DPS/Healer w walce), więc naprawić najpierw. Problem 2 wymaga zmian w dwóch plikach, ale nie koliduje z Problemem 1 (inne stany). Oba można robić równolegle przez dwóch agentów, ale latch-y nie mogą dzielić tych samych pól (`_latchedBackline*` vs `_pullCommitted*` — różne).

---

## 5. Szczegóły implementacyjne dla agenta

### 5.1. Gdzie dodać pola i stałe

- **PartyGrindBot.cs** — na górze klasy, obok istniejących pól:
  ```csharp
  private long _lastIpcSendUtcMs;
  private long _pullStartedUtcMs; // już istnieje
  private ulong _currentFocusGuid; // już istnieje
  // DODAĆ:
  private ulong _pullCommittedGuid;
  private long _pullCommittedUntilMs;
  private WoWPoint _latchedBacklinePos;
  private ulong _latchedBacklineTargetGuid;
  private long _latchedBacklineUntilMs;
  private long _reachedBacklineAtMs;
  ```

- **SquadCoordinatorService.cs** — nowe pola debounce obok `_isWaitingForFollower`, `_isHardBacktracking`.

- **SquadSettings** — rozważyć dodanie konfigurowalnych stałych (opcjonalnie):
  ```csharp
  int BacklineEnterDistance = 8;
  int BacklineExitDistance = 3;
  int PullCommitTimeoutSeconds = 10;
  int DistanceDebounceMs = 2000;
  ```
  Jeśli nie ma czasu, zahardkodować w `PartyGrindBot` i wystawić w `SquadSettings` w kolejnym PR.

### 5.2. Jak nie zepsuć istniejącego zachowania

- **Nie zmieniać** `SquadFormationService.ComputeStandoffPosition` matematyki — tylko dodać latch w `PartyGrindBot`.
- **Nie zmieniać** `SquadTankBridge.CanInitiatePull` kontraktu publicznego — dodać przeciążenie z `pullCommittedGuid` zamiast modyfikować istniejącą sygnaturę (zachować kompatybilność testów).
- **Nie ruszać** `Looting`, `TownRun`, `CorpseRun`, `Provisioning` — ich logi są poprawne.
- Przy `Stop()` czyścić wszystkie nowe pola (jak `_currentFocusGuid`).

### 5.3. Logowanie do debugowania (tymczasowe)

Dodać `Logging.WriteDebug` gdy commit ustawiony/zwolniony, aby w logach widać było `Pull commit: Prairie Stalker Guid=... until=...` i `Pull commit released: timeout/dead`. Po weryfikacji usunąć lub zostawić na `DEBUG`.

### 5.4. Obsługa edge case

- **Mob za ścianą / Unreachable:** `Navigator.MoveTo` zwróci `PathGenerationFailed` → `SquadApproach.TryMove` już ma retry 15s, ale `PullCommit` timeout 10s i tak go zwolni → tank pójdzie do hotspotu z nowym mobem.
- **Stuck przy Burning Embers (50803):** `IsStuck` event w logach (`Trying jump`) — `PullCommit` nie powinien blokować `Stuck` handlera; `Stuck` ma własną logikę w `Styx.Logic.Pathing`.
- **Tank umiera w trakcie PullCommit:** `UpdateCoordinatorTargets` ma `local.IsDead` → zwolnij commit, przejdź do `CorpseRun` (priorytet 2).
- **Wielu tanków?** Nie dotyczy — tylko `TankLeader` ma `IsCoordinator=true`.

---

## 6. Kryteria akceptacji (Definition of Done)

### Problem 1

- [ ] Healer `Ksionc` log: `Moving to backline` <20 na 30min, `Running heal`/`Healer assisting` dominują.
- [ ] DPS `Bedoes` log: brak `Combat → Backline` <2s, ciągłe casty `Corruption → Shadow Bolt → Immolate`.
- [ ] `MeshNavigator Skipped` między `Combat` a `Backline` = 0.
- [ ] Unit testy `BacklineHysteresis_*` przechodzą.

### Problem 2

- [ ] Tank `Bedogie` log: brak `Pull: X → hotspot → Pull: X` na tym samym X <3s.
- [ ] `Pull → Combat` >80% (przed <50%).
- [ ] `Skipped` -70% w sekcjach pull.
- [ ] Unit testy `PullCommit_*`, `DebouncedWaiting_*` przechodzą.

### Ogólne

- [ ] `dotnet build CopilotBuddy.csproj -c Release /p:Platform=x86` przechodzi.
- [ ] `dotnet test Tools/CopilotBuddy.PartyGrind.Tests -c Release /p:Platform=x86` — wszystkie istniejące testy zielone.
- [ ] Logi regresji: `TownRun`, `Looting`, `CorpseRun` bez zmian.
- [ ] PR zawiera opis z linkiem do `DIAGNOZA_PLAN.md` i wynikami weryfikacji logów.

---

## 7. Instrukcja dla agenta — krok po kroku

1. **Przeczytaj** `DIAGNOZA_PLAN.md` i ten plan w całości.
2. **Utwórz branch** z `master` (lub użyj `arena/e3d71216-copilotbuddy`), nie modyfikuj `DIAGNOZA_PLAN.md`.
3. **Zaimplementuj Etap 1** (TASK 1.1-1.2) → `dotnet build` → `dotnet test` → commit `fix: backline hysteresis + latch`.
4. **Zaimplementuj Etap 2** (TASK 1.3) → build/test → commit `fix: prefer combat over formation when in range+LoS`.
5. **Zaimplementuj Etap 3** (TASK 2.1) → build/test → commit `fix: tank pull commit lock`.
6. **Zaimplementuj Etap 4** (TASK 2.2-2.3) → build/test → commit `fix: distance debounce for pull`.
7. **Dodaj testy** (TASK 1.5, 2.5) → `dotnet test` musi być zielony → commit `test: add hysteresis and pull commit tests`.
8. **Przeprowadź weryfikację logów** — uruchom bota w WoW 3.3.5a na trasie Mulgore z 3 klientami (Paladin 9 + Warlock 8 + Priest 9), zbierz 30min logów, porównaj `grep` metryki z kryteriami akceptacji, dołącz fragmenty logów do PR.
9. **Otwórz PR** wg `CONTRIBUTING.md` — tytuł `PartyGrind: fix backline and tank pull loops`, opis z linkiem do diagnozy i wynikami weryfikacji.

**Zakaz:** nie edytuj `Offsets335.txt`, `data.bin`, `Navigation.dll`, `output.zip` bez potrzeby; nie dodawaj sekretów.

---

## 8. Załączniki — gotowe komendy weryfikacji

```powershell
# Liczba backline vs combat (Problem 1)
Select-String "Moving to backline" 2026-11-20_1249_15728.log | Measure-Object
Select-String "Combat routine" 2026-11-20_1249_15728.log | Measure-Object

# Przeplot Combat↔Backline <5s (Python z diagnozy)
python verify_backline.py /tmp/logs/*.log

# Tank pull vs hotspot
Select-String "Pull routine: Prairie Stalker" 2026-11-20_1006_5564.log | Measure-Object
Select-String "Moving to grind hotspot" 2026-11-20_1006_5564.log | Measure-Object
Select-String "Skipped" 2026-11-20_1006_5564.log | Measure-Object

# Korelacja Skipped obok Pull/hotspot (powinno być 0 po fixie)
Select-String -Pattern "Skipped|Pull routine|Moving to grind hotspot" log.txt

# Build i testy
dotnet build CopilotBuddy.csproj -c Release /p:Platform=x86
dotnet test Tools/CopilotBuddy.PartyGrind.Tests/CopilotBuddy.PartyGrind.Tests.csproj -c Release /p:Platform=x86
```

---

*Koniec planu. Agent powinien traktować ten plik jako checklistę i nie pomijać weryfikacji logów przed PR.*
