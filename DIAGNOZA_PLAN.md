# Diagnoza zapętleń bota Party Grind (WotLK 3.3.5a)

**Branch:** `arena/e3d71216-copilotbuddy` (bazuje na `master` commit `2effa4da`)  
**Data analizy:** 2026-11-20 (logi z sesji `10:06` i `12:49`, 10 plików `*.log` dostarczonych w `/tmp/arena-coding-uploads`)  
**Zakres:** wyłącznie diagnoza i plan zmian — bez modyfikacji kodu.

---

## Kontekst wykonawczy bota

Drzewo zachowań `PartyGrindBot.BuildRootBehavior()` (prioritet `RoutineAwareRoot`):

1. `Paused / disconnected` → `MoveStop`
2. `Dead / CorpseRun` → `MoveTo(corpse)` / `RepopMe()`
3. `TownRun` → `SquadTownRunService.RunTownTick`
4. **`Combat & Role Bridges`** → `RunCombatAndRoleBehavior()` (decorator `IsCombatOrHealNeeded()`)
5. `PostCombat Resurrection` → `SquadHealBridge.RunPostCombatResurrectionTick`
6. `Resting` → `Rest.Feed/Drink`
7. `Provisioning` → `SquadProvisioningService`
8. `OutOfCombat Buffs`
9. `Looting` → `Navigator.MoveTo(loot)`
10. **`Zone Migration / Travel / Pull / Roam`** → `RunTravelOrPullBehavior()` (ostatni fallback)

Reguła ogólna: pierwszy decorator którego warunek jest prawdziwy **przerywa** łańcuch i blokuje wszystkie niższe priorytety w tym tiku. Kolejny tick zaczyna od góry od nowa.

Role w logach:
- **TankLeader** — `Bedogie` / `Bedogie` (Coordinator, `IsCoordinator=True`) → logi `1006_5564`, `1249_20932`
- **DpsRanged** — `Bedoes` (Warlock), `Bedoes` (Mage), `Bedoes` (Mage 9) → `1006_1668`, `1006_4140`, `1249_10688`, `1249_18712`
- **Healer** — `Ksionc` (Troll Priest 9) → `1006_12988`, `1249_15728` — najczyściej pokazuje Problem 1
- **Druid/DpsRanged** Hybrid — `1249_20488` (bez backline, mniej istotny dla pętli)

---

## Problem 1 (poważny): DPS i Healer zapętlają `Moving to backline position`

### Źródło pętli — konkretne wpisy i stany

**Logi dowodowe:**

- `2026-11-20_1006_1668.log:197` `10:08:58.672 Moving to backline` → `206 10:09:01.437 Combat routine: Prairie Wolf` → `209 10:09:01.808 Moving to backline` (delta 0.37 s)
- `1006_1668:251 Combat Prairie Wolf` → `254 10:10:24.824 Moving to backline` (1.47 s) → kolejna walka przed wykończeniem poprzedniej pozycji
- `1249_15728.log` — **199×** `Moving to backline position` i **0×** `Combat routine` (czysty Healer): log Healer-a `Ksionc` w trybie `Healer (Coordinator=False)`. Fragment krytyczny (linie 190–217):
  ```
  12:51:15.124 Moving to backline
  12:51:18.428 Healer assisting attack (rzadko, gdy przypadkiem Distance2D <3)
  12:51:20.517 Moving to backline
  12:51:29.760 Moving to backline → 12:51:32.394 Following Bedogie → 12:51:34.920 Moving to backline → 12:51:35.118 Following → …
  12:51:38.011 Moving to backline → Following → …
  ```
  Ten sam wzór w `1249_10688.log` (Warlock DPS): `12:51:19.068 Moving to backline` → `12:51:19.576 Combat Elder Plainstrider` → `12:51:30.478 Moving to backline` itd., przeplot z `Healer assisting` i `Running heal routine`.
- Wszystkie logi Healerów/DPS-ów z porannej sesji (`1006_12988`, `1006_1668`, `1006_4140`) pokazują identyczny przeplot `Combat routine ↔ Moving to backline` w odstępach < 5 s (skrypt Py w analizie wykrył 8 takich przełączeń w `1006_1668` i 7 w `1249_10688`).
- `MeshNavigator Skipped 2 path nodes` pojawia się między nimi (np. `1006_1668:210`, `1249_15728:231-232`), co potwierdza że `Navigator.MoveTo` jest przerywane zanim ścieżka zostanie ukończona.

**Stany w których pętla się pojawia:**

- **Dla DPS (`PartyGrindBot.RunDpsBehavior`)** — stan 4 (`Combat & Role Bridges`) przed wejściem do `RunRoutineCombat`. Decyzja jest w `RunDpsBehavior` linia ~710: `if (PositionBackline(...)) return Success;` które **wyprzedza** `return RunRoutineCombat(focusGuid);`
- **Dla Healer-a (`PartyGrindBot.RunCombatAndRoleBehavior` gałąź `Healer`)** — identycznie: `if (PositionBackline(HealerStandoffDistance...)) return Success;` wyprzedza `TryHealerAssistDps`.
- **Dla HybridOffHeal** — ten sam kod przez `RunDpsBehavior(isMelee:false)` gdy `emergencyOnly` nie aktywne.

W obu przypadkach pętla zachodzi **w ramach jednego dekoratora (priorytet 4)** — nie jest to rywalizacja między Combat a Travel, tylko **wewnętrzne przełączanie w obrębie `RunCombatAndRoleBehavior`**: jeden tick wykonuje `PositionBackline` (log `Moving to backline`), następny tick (lub nawet ten sam sekundowy okres) wykonuje `RunRoutineCombat` (log `Combat routine`), zanim dystans do `slotPos` został zredukowany poniżej progu.

### Przyczyna techniczna — która decyzja przełącza

1. **Brak histerezy w `PositionBackline` (`PartyGrindBot.cs:720-740`).**  
   Warunek wejścia i wyjścia to ten sam próg: `Distance2D(slotPos) > 3.0f` → `Navigator.MoveTo`. Gdy postać jest >3 jarda od slotu, bot idzie do tyłu; gdy <3, wykonuje combat. W następnym tiku slot jest **przeliczany od nowa**:
   ```csharp
   WoWPoint slotPos = SquadFormationService.ComputeStandoffPosition(
       tankPos, leader.Facing, focusPos, standoffDistance, slotIdx, slotCount);
   // standoffDistance 18–24 yd, lateral +/-4.5 yd, slotIdx zależny od rosteru
   ```
   `tankPos` = `leader.X/Y/Z` aktualizowane co 100 ms przez IPC, `leader.Facing` zmienia się gdy tank obraca się do moba, `focusPos` = pozycja moba (która też się rusza). Każde przesunięcie tanka/moba o 1–2 yd przesuwa `slotPos` o tę samą wartość. DPS który szedł 15 yd do tyłu, po 1 s znajduje się 2.5 yd od starego slotu, ale nowy slot jest już 4 yd dalej → warunek `>3` znów prawdziwy → abort combat i marsz od nowa. Stąd logi `Skipped 2 path nodes` — MeshNavigator porzuca poprzednią ścieżkę bo `MoveTo` dostał nowy cel.

2. **Pozycjonowanie ma wyższy priorytet niż walka zasięgowa.**  
   DPS zasięgowy (Warlock, Mage) może czarować z 30 yd (`target.Distance > 30 || !InLineOfSpellSight` jest sprawdzane dopiero w `TryHealerAssistDps`, ale nie w `PositionBackline`). Bot wymaga idealnej formacji **nawet gdy cel jest w zasięgu i widoczności**. W logu `1006_1668:206-209` Warlock zdążył rzucić `Corruption` (`Combat routine`) i natychmiast został cofnięty do tyłu — mimo że był 5 yd od moba (Singular `Melee Distance:5`) i mógł kontynuować.

3. **Brak blokady na czas castu/ruchu.**  
   `PositionBackline` nie sprawdza `me.IsCasting`, `me.IsMoving`, `Spell_C::CastSpell` w toku, ani czy `RunRoutineCombat` właśnie zwrócił `Running`. `TickRoutine` ustawia `_combatTicked=true`, ale już w następnym tiku `PositionBackline` znowu wygrywa, bo odległość do nowego slotu >3. Dla Healer-a sytuacja jest ostrzejsza: `RunHealerTick` zwraca `true` tylko gdy jest kogo leczyć; gdy nikogo nie trzeba, natychmiast przechodzi do `PositionBackline`, więc Healer spędza całe walki maszerując (± 1.5 s na ścisnięcie, widoczne w `1249_15728` gdzie `Following Bedogie` i `Moving to backline` przeplatają się co 1–3 s).

4. **Dodatkowy czynnik — `Distance2D` vs `Distance3D`.**  
   Kod używa `Distance2D` (ignoruje Z). Na pagórkowatym terenie Mulgore (stuck przy `Burning Embers` `50803`) różnica Z powoduje że bot nigdy nie osiąga `<=3` w 2D, mimo że w 3D byłby bliżej.

### Proponowana zmiana — logika, bez kodu

**Cel:** DPS/Healer kończy walkę/leczenie zanim wróci na pozycję z tyłu; pozycja jest korygowana tylko gdy jest to konieczne dla zasięgu/LoS lub po zakończeniu walki.

Minimalna zmiana w obrębie `RunDpsBehavior` i gałęzi `Healer` w `RunCombatAndRoleBehavior` + `PositionBackline`:

1. **Zamień jednostronny próg 3.0 na histerezę dwuprogową z zatrzaskiem (latch).**
   - Próg wejścia do marszu: `Distance2D > 8–10 yd` (lub `> standoffDistance * 0.4`).  
   - Próg wyjścia (uznanie pozycji za osiągniętą): `Distance2D < 3 yd` **i** pozostanie w tym promieniu przez ≥ 0.5 s.  
   - Po osiągnięciu `slotPos` zatrzaśnij (latch) ten slot na czas trwania walki: nie przeliczaj `ComputeStandoffPosition` dopóki `leader.FocusTargetGuid` się nie zmieni, mob nie umrze, lub tank nie oddali się o >10 yd od pozycji gdzie slot został zatrzaśnięty. Aktualny kod przelicza slot co tick — latch eliminuje dryf.

2. **Odwróć priorytet: walka przed formacją gdy cel osiągalny.**  
   Przed `PositionBackline` dodaj warunek:
   ```
   if (me.Combat || focus.InCombat || focus.IsTargetingMyPartyMember)
       jeśli focus.Distance <= MaxSpellRange (30 yd dla ranged, 35 yd dla heal)
           i focus.InLineOfSpellSight
           i !me.IsMoving (lub me.IsCasting)
       → nie wykonuj PositionBackline, idź od razu do RunRoutineCombat / HealerAssist
   ```
   Czyli: postać w walce, która ma LoS i zasięg, **kontynuuje** czarowanie zamiast marszu. Pozycjonowanie wykonuje się tylko gdy:
   - poza zasięgiem, bez LoS, lub
   - `IsCombatOrHealNeeded` jest fałszywe (poza walką), lub
   - po zakończeniu `RunRoutineCombat` zwróciło `Failure` z powodu dystansu (wtedy `SquadApproach.TryMove` już próbuje podejścia).

3. **Nie przerywaj casta i nie nadpisuj aktywnej ścieżki combat.**  
   Jeśli `_activeCombatRoutine` jest w stanie `Running` lub `me.IsCasting != 0`, `PositionBackline` zwraca `false`. To samo dla Healer-a: jeśli `_activeHealBehavior` `Running`, nie marsz. Zapobiega 0.37 s przełączeniom widocznym w `1006_1668:206→209`.

4. **Różnicuj tryby: `InCombat` vs `OOC formation`.**  
   Gdy `me.Combat == false` i `leader.InCombat == true` ale `me` nie ma jeszcze aggro, dozwolone jest jednorazowe podejście do backline (np. na początku pulla, `DpsEngageDelayMs`). Potem, gdy `me.Combat` staje się `true`, latch z pkt 1 blokuje dalsze korekty.

Zasięg zmiany: tylko `PartyGrindBot.cs` metody `PositionBackline`, `RunDpsBehavior`, `RunCombatAndRoleBehavior` (gałąź Healer) oraz ewentualnie stała `SquadFormationService.ComputeStandoffPosition` (dodanie parametru `useLatchedSlot`). Nie wymaga przebudowy `SquadCoordinatorService` ani profilu.

### Weryfikacja — jakie wpisy potwierdzą zniknięcie pętli

Po wdrożeniu, w logach tego samego typu sesji (Healer `Ksionc`, DPS `Bedoes`):

- **Spadek `Moving to backline position` o >80%** w trakcie walki: przed poprawką 99–199 wpisów na 30 min sesji walki; po poprawce oczekiwane <20, wyłącznie na początku pulla lub gdy mob ucieka poza 30 yd.
- **Brak przeplotu `Combat routine → Moving to backline` w odstępie <2 s.** W `grep` time-delta skryptem (jak w analizie) liczba par `Combat ↔ Backline` z `delta <5 s` spada do 0. Zamiast tego ciągłe bloki:
  ```
  Moving to backline (raz, na start pulla)
  Combat routine: X (wielokrotnie, 5–15 s)
  Combat routine: X
  ...
  Moving to loot / Following
  ```
  bez wtrąceń `Moving to backline` w środku walki.
- **Zniknięcie `MeshNavigator Skipped 2 path nodes` między `Combat` a `Backline`.** Przed poprawką Skipped pojawia się 65–169× na log (wszystkie logi) w towarzystwie backline; po poprawce Skipped zostaje tylko przy rzeczywistych przeszkodach terenowych, nie przy korekcie formacji.
- **Healer:** wzrost `Running heal routine` / `Healing Y with Z` i `Healer assisting attack` kosztem `Moving to backline`. Healer `Ksionc` przed poprawką ma 0 `Combat routine` i 199 `Moving to backline`; po poprawce powinien mieć proporcję odwrotną gdy grupa jest w walce (np. 30 `Combat/Heal` na 10 `Backline`).
- **DPS:** ciągłość castów w Singular: `Casting Corruption → Casting Shadow Bolt → Casting Immolate` bez wtrąceń `Moving to backline` pomiędzy (jak w zdrowym fragmencie `1006_1668:215-220`).

---

## Problem 2: Tank w kółko zmienia cel i pozycję (`Roam/Pull` vs `Moving to grind hotspot`)

### Źródło pętli — konkretne wpisy i stany

**Logi dowodowe:**

- **Tank `Bedogie` (Coordinator) `1006_5564.log`** — typowa pętla, linie 449–485 (11:55:46–11:56:11):
  ```
  11:55:46.398 Pull routine
  11:55:46.595 Pull routine: Prairie Stalker
  11:55:47.058 Skipped 2 path nodes
  11:55:47.852 Skipped 2 path nodes
  11:55:48.314 Moving to grind hotspot
  11:55:48.416 Skipped 7 path nodes
  11:55:48.611 Waiting for squadmate
  11:55:51.696 Pull routine: Prairie Stalker
  11:55:52.059 Skipped 3
  11:55:52.725 Skipped 3
  11:55:53.185 Moving to grind hotspot
  11:55:53.288 Skipped 6
  11:55:53.680 Waiting for squadmate
  11:55:56.065 Pull routine: Prairie Stalker
  11:55:58.715 Moving to grind hotspot → 11:55:58.818 Skipped 9 → 11:55:59.178 Pull routine: Prairie Stalker → 11:56:01.799 Moving to grind hotspot → …
  ```
  Ten sam mob `Prairie Stalker` wybierany 5× w ciągu 15 s, za każdym razem przerwany przez `Moving to grind hotspot`. W całym logu `1006_5564` `Pull routine: Prairie Stalker` 46× i `Moving to grind hotspot` 100× przeplatają się na przemian; `Skipped` 147× — zawsze **po** zmianie celu ruchu.

- **Tank `Bedogie` `1249_20932.log`** — intensywniejsza wersja, linie 214–227 (12:51:32–12:51:46):
  ```
  12:51:32.309 Moving to grind hotspot
  12:51:34.891 Pull routine: Flatland Cougar
  12:51:36.443 Skipped 2
  12:51:37.429 Moving to grind hotspot
  12:51:37.961 Pull routine: Flatland Cougar
  12:51:38.867 Moving to grind hotspot
  12:51:38.995 Pull routine: Flatland Cougar
  12:51:39.243 Moving to grind hotspot
  12:51:39.785 Pull routine: Flatland Cougar
  12:51:41.301 Moving to grind hotspot
  12:51:41.611 Pull routine: Flatland Cougar
  12:51:43.093 Moving to grind hotspot
  12:51:43.398 Pull routine: Flatland Cougar
  12:51:46.373 Combat routine: Flatland Cougar   // dopiero tutaj walka się zaczęła
  ```
  oraz linie 235–276 gdzie `Combat routine: Swoop / Flatland Cougar` przeplatają się co 0.3 s (`Singular Switched targets!` co tick), co pokazuje że tank nie może zdecydować który mob bić, bo `Targeting` co chwilę dostaje nowego `FirstUnit`.

- **Korelacja `Skipped N path nodes` z `MeshNavigator`.** We wszystkich logach tanków `Skipped` występuje wyłącznie w bezpośrednim sąsiedztwie zmiany `Pull routine ↔ Moving to grind hotspot` (np. `1006_5564:169-170 Pull → Skipped → Moving to grind`, `1249_20932:324-325 Skipped → Pull`). `Bedogie` `1006_5564` ma `Skipped` 147×, `1249_20932` 169× — oba najwyższe wartości wśród 10 logów, dokładnie w plikach gdzie pętla tanka jest opisana w zadaniu.

**Stany w których kolizja się pojawia:**

- **Stan `Roam/Pull`** = `PartyGrindBot.RunTravelOrPullBehavior()` gałąź `IsCoordinator` linie ~1330–1375:
  ```csharp
  if (CanInitiatePull(...)) { target = Targeting.FirstUnit; _currentFocusGuid=target.Guid; RunRoutineCombat; }
  // 5. Roam
  if (grindArea.CurrentHotSpot ...) { Navigator.MoveTo(hotspot, "Roaming hotspot"); }
  ```
- **Stan `Moving to grind hotspot`** = ten sam `RunTravelOrPullBehavior` fallback (`Roam`), log `Moving to grind hotspot`
- **Stan `Pull routine: Prairie Stalker`** = albo `RunTravelOrPullBehavior` gdy `CanInitiatePull` prawdziwe, albo `RunCombatAndRoleBehavior` (TankLeader → `RunRoutineCombat`) gdy `IsCombatOrHealNeeded` prawdziwe (bo `_currentFocusGuid !=0`). Oba logują `Pull routine: X` gdy `!me.Combat` (Singular używa `PullBehavior`).

Kolizja nie dotyczy `Looting` jako głównego źródła — `Looting` w logach tanka pojawia się prawidłowo po walce (`1249_20932:332-338 Looting Flatland Cougar → Moving to grind hotspot`), ale nie przerywa pulla w środku podejścia. Dane potwierdzają że `Looting` nie jest częścią pętli pull/hotspot (w `1006_5564` `Looting` 86×, ale pętla pull/hotspot występuje także gdy `Looting` nie jest aktywne). Dlatego zgodnie z poleceniem `Looting` uwzględniamy tylko jako wykluczone.

### Przyczyna techniczna — który stan nadpisuje drugi

1. **Dwa niezależne źródła `_currentFocusGuid / celu ruchu` bez blokady.**  
   - Źródło A: `UpdateCoordinatorTargets()` (wywoływane co 100 ms z `PulseCoordinator`) ustawia `_currentFocusGuid` na `Targeting.FirstUnit` gdy `!localCannotPull && Phase==Grinding && !IsWaitingForFollower && CanInitiatePull`. Gdy którykolwiek warunek zawiedzie, **zeruje** guid (`newFocusGuid=0`).  
   - Źródło B: `RunTravelOrPullBehavior()` ustawia `_currentFocusGuid` na `FirstUnit` gdy `CanInitiatePull` prawdziwe, w przeciwnym razie idzie do `Roam`.  
   Oba stany wywołują `Navigator.MoveTo` z różnym celem: A/B → `target.Location` (mob), Roam → `hotspot.Position`. Ponieważ `BuildRootBehavior` wykonuje jeden tick jako **albo** Combat (priorytet 4) **albo** Travel (priorytet 10), ale **FocusGuid jest współdzielony**, każda zmiana FocusGuid między tikami powoduje że następny tick wybierze drugi stan. Przykład:
   - Tick N: `FocusGuid = Prairie Stalker Guid` → `IsCombatOrHealNeeded` true (bo `FocusGuid !=0`) → `RunCombatAndRoleBehavior` → `Pull routine: Prairie Stalker` → `Navigator.MoveTo(mob)`  
   - Tick N+1: `CanInitiatePull` zwraca `false` (bo followerzy oddalili się > `MaxPullGatherDistance` podczas gdy tank szedł do moba, lub `IsWaitingForFollower` stało się true) → `UpdateCoordinatorTargets` zeruje FocusGuid → `IsCombatOrHealNeeded` false → dekorator 4 nie wchodzi → spada do priorytetu 10 → `RunTravelOrPullBehavior` → `CanInitiatePull` false → `Moving to grind hotspot` → `Navigator.MoveTo(hotspot)` **nadpisuje** poprzednie `MoveTo(mob)` → `MeshNavigator` loguje `Skipped 2 path nodes` (porzucone węzły starej ścieżki).

   **Wniosek: To Roam (`Moving to grind hotspot`) nadpisuje cel ruchu Pull-a, ale przyczyną nadpisania jest zerowanie `FocusGuid` w `UpdateCoordinatorTargets` / `CanInitiatePull`.** Bezpośrednio w kodzie, `MoveTo(hotspot)` jest wywołany po tym jak `MoveTo(mob)` nie zdążył dobiec do końca.

2. **Oscylujący warunek `CanInitiatePull` vs `SoftWaitDistance / MaxPullGatherDistance`.**  
   `CanInitiatePull` sprawdza w pętli po wszystkich `Members`: `dist > MaxPullGatherDistance → false`. `IsWaitingForFollower` sprawdza `maxFollowerDist > SoftWaitDistance`. Gdy tank idzie do moba oddalając się od grupy, dystans rośnie → w pewnym momencie przekracza `MaxPullGatherDistance` (np. 40 yd) lub `SoftWaitDistance` → pull zostaje odwołany → tank zawraca do hotspota (który leży bliżej grupy, więc dystans maleje) → warunki znowu spełnione → pull wznowiony na tego samego moba (`Prairie Stalker` 5× pod rząd). To histereza dystansowa bez histerezy czasowej.

3. **Brak blokady przejścia `Pull → Roam` podczas aktywnego podejścia.**  
   `RunTravelOrPullBehavior` nie sprawdza czy `Navigator` jest już w trakcie `MoveTo(mob)` (Singular PullBehavior też wywołuje `Navigator.MoveTo` wewnętrznie). Nie ma flagi `isPullInProgress`. Dlatego każdy tick może zmienić zdanie.

4. **Dodatkowy efekt `Skipped`:**  
   `Navigator.MoveTo` w PartyGrind i w Singular PullBehavior konkurują o ten sam `Navigator`. Gdy PartyGrind wywoła `MoveTo(hotspot)` zaraz po tym jak Singular wywołał `MoveTo(mob)` w poprzednim tiku, stary path jest unieważniony i `MeshNavigator` musi wyliczyć nową ścieżkę — log `Skipped N path nodes` oznacza że N węzłów starej ścieżki zostało pominiętych bo nowy cel jest w inną stronę. Im więcej skoków, tym wyższy `Skipped` (do 9 w `1006_5564:468`).

### Proponowana zmiana — reguła priorytetów / blokada przejścia

**Cel:** Tank kończy jedną akcję (pull) zanim przejdzie do drugiej (roam), bez przebudowy całej logiki.

1. **Wprowadź blokadę `PullCommit` z timeoutem i warunkiem zwolnienia.**  
   Gdy `UpdateCoordinatorTargets` lub `RunTravelOrPullBehavior` ustawi `_currentFocusGuid` na niezerowy (commit do moba A), ustaw `_pullStartedUtcMs = now` (już istnieje) oraz flagę `pullCommitted = true`. Blokada trwa dopóki nie zajdzie **jeden** z warunków zwolnienia:
   - `targetGuid` nie istnieje / `Dead` / `IsFriendly` / `Distance > 60 yd` (mob uciekł/zdespawnował)
   - `me.Combat == true` i `target.Combat == true` (pull się udał, mob nas bije)
   - `Now - pullStarted > PullCommitTimeoutMs` (np. 8000–12000 ms) — zapobiega wiecznemu bieganiu za niedostępnym mobem (np. za ścianą, `Unreachable` że tank utknął w `Burning Embers`)
   - `target.HealthPercent` spadło poniżej 100% i `target.IsTargetingMyPartyMember` (ktoś inny zaczął walkę)

   Dopóki blokada aktywna, **żaden** z poniższych nie może wyzerować FocusGuid ani wywołać `Moving to grind hotspot`:
   - `UpdateCoordinatorTargets` nie zeruje `newFocusGuid` z powodu `IsWaitingForFollower` / `MaxPullGatherDistance` / `localCannotPull` — zamiast tego pozostawia stary guid.
   - `RunTravelOrPullBehavior` pomija gałąź `Roam` i `Waiting for squadmate` — zamiast `MoveStop` przy `IsWaitingForFollower`, tank kontynuuje podejście do commited moba (lub staje jeśli w `HardBacktrack` odległość > `HardBacktrackDistance`, ale nie skręca do hotspota).

2. **Reguła priorytetów: Pull > Roam > Wait.**  
   W `RunTravelOrPullBehavior` kolejność priorytetów powinna być jawnie zakodowana:
   ```
   if (pullCommitted && committedTarget still valid)
        → kontynuuj Navigator.MoveTo(committedTarget) (nawet jeśli CanInitiatePull chwilowo false)
   else if (IsHardBacktracking) → Backtrack
   else if (IsWaitingForFollower && !pullCommitted) → Wait
   else if (CanInitiatePull) → nowy Pull
   else if (Roam) → Moving to grind hotspot
   ```
   Obecnie `IsWaitingForFollower` jest przed `CanInitiatePull`, ale `Roam` jest w gałęzi `else` pulla, więc gdy `pullCommitted` nie istnieje, `Roam` wygrywa nad kontynuacją pulla. Po zmianie, committed pull ma najwyższy priorytet.

3. **Histereza dystansowa z opóźnieniem czasowym.**  
   Sam timeout z pkt 1 nie wystarczy jeśli followerzy ciągle oscylują wokół `SoftWaitDistance`. Dodaj 2-sekundowe opóźnienie (debounce) na wejście/wyjście z `IsWaitingForFollower` i `IsHardBacktracking` w `SquadCoordinatorService.EvaluateTick` dla decyzji pull: `CanInitiatePull` powinno patrzeć na `IsWaitingForFollowerDebounced` (wartość sprzed 2 s), nie natychmiastową. Alternatywnie, w `CanInitiatePull` ignoruj `MaxPullGatherDistance` gdy `pullCommitted` true i dystans < `MaxPullGatherDistance + 15 yd` (bufor).

4. **Synchronizuj `UpdateCoordinatorTargets` i `RunTravelOrPullBehavior` na tym samym `CanInitiatePull` + `pullCommitted`.**  
   Obecnie `UpdateCoordinatorTargets` ma własną kopię warunków (`localCannotPull || Phase != Grinding || IsWaiting || !CanInitiatePull`) która może wyzerować guid tuż po tym jak `RunTravelOrPullBehavior` go ustawił. Po wprowadzeniu blokady, oba miejsca muszą respektować `pullCommitted`: jeśli committed, nie zeruj.

Zakres zmiany: `PartyGrindBot.cs` (`UpdateCoordinatorTargets`, `RunTravelOrPullBehavior`, nowe pole `pullCommittedUntil`), oraz `SquadCoordinatorService.cs` (histereza czasowa dla `IsWaitingForFollower` lub przekazanie `pullCommitted` jako wyjątku). Bez zmian w `SquadFormationService`, `LootTargeting`, ani profilu.

### Weryfikacja — jakie wpisy potwierdzą zniknięcie pętli

Po wdrożeniu, w logach tanka `Bedogie` (`1006_5564`, `1249_20932`):

- **Brak przeplotu `Pull routine: Prairie Stalker → Moving to grind hotspot → Pull routine: Prairie Stalker` na tym samym mobie w odstępie <3 s.** Przed poprawką ten wzór występuje 20–30× na sesję (np. `1249_20932:214-226` 6× w 9 s). Po poprawce oczekiwany wzór:
  ```
  Pull routine: Prairie Stalker (raz)
  Combat routine: Prairie Stalker (po dojściu)
  Combat routine: Prairie Stalker
  ...
  Looting / Moving to grind hotspot (dopiero po śmierci/loocie)
  ```
  Jeśli mob jest nieosiągalny, po `PullCommitTimeoutMs` pojawia się **jeden** `Moving to grind hotspot` i nowy `Pull routine` już z innym guid (inny mob), nie tym samym.

- **Spadek `Skipped N path nodes` o >70% w sekcjach pull.** Przed poprawką 147–169 `Skipped` na log tanka, z czego >80% w klastrach pull/hotspot. Po poprawce `Skipped` zostaje tylko przy omijaniu przeszkód terenowych (np. `50803 Burning Embers` w Mulgore), nie przy zmianie celu pull ↔ hotspot. W `grep` korelacja `Skipped` w odległości ≤2 linii od `Pull routine` lub `Moving to grind hotspot` spada do 0.

- **Wzrost ciągłości `Combat routine: X` po `Pull routine: X`.** Przed poprawką `Pull` często nie przechodzi w `Combat` (mob nie został uderzony bo tank zawrócił). Po poprawce każdy `Pull routine: Prairie Stalker` który nie został anulowany timeoutem, kończy się blokiem `Combat routine: Prairie Stalker` (Singular faktycznie rzuca `Judgement`, `Lesser Heal` etc., jak w zdrowym fragmencie `1249_20932:355-358`).

- **Stabilny `Waiting for squadmate` bez późniejszego `Moving to grind hotspot` w tej samej sekundzie.** Jeśli followerzy są daleko, tank loguje `Waiting for squadmate` i **stoi** (MoveStop) zamiast oscylować do hotspota i z powrotem. W logach nie pojawia się już sekwencja `Pull → Moving to grind → Waiting → Pull` w odstępie 1 s (jak `1006_5564:457-460`).

- **`Looting` pozostaje nienaruszone:** po zabiciu `Flatland Cougar` (`1249_20932:332-338 Looting Flatland Cougar → Moving to grind hotspot`) sekwencja loot → hotspot jest nadal obecna, ale nie występuje już w środku pulla. To potwierdza że `Looting` uwzględniono tylko gdy dane je potwierdzają i nie jest źródłem pętli.

---

## Najbardziej prawdopodobna przyczyna (synteza)

Dla obu problemów wspólnym mianownikiem jest **brak histerezy i blokady przejścia** w obliczaniu celu ruchu co tick na podstawie świeżych pozycji tanka/moba/followerów.

- **Problem 1:** `ComputeStandoffPosition` przelicza idealny slot za tankiem co 100 ms na podstawie `tankPos + facing + focusPos`. Próg 3 yd bez latcha powoduje że każda korekta tanka/moba unieważnia poprzedni `MoveTo` i wymusza nowy marsz, przerywając `RunRoutineCombat` zanim cel zostanie uznany za osiągalny (w zasięgu 30 yd i LoS). Najbardziej prawdopodobny wyzwalacz to **obrót tanka w stronę moba (zmiana `Facing`)** w momencie pulla — slot za tankiem przesuwa się o 5–8 yd, DPS staje się “za daleko” i zawraca.

- **Problem 2:** `CanInitiatePull` zależy od dystansu do followerów, który rośnie gdy tank idzie do moba. Przekroczenie `MaxPullGatherDistance`/`SoftWaitDistance` powoduje wyzerowanie `_currentFocusGuid` i natychmiastowy fallback do `Roam (Moving to grind hotspot)`, który nadpisuje `MoveTo(mob)`. Gdy tank zawraca do hotspota, dystans maleje i cykl się powtarza na tym samym `Prairie Stalker`. Najbardziej prawdopodobny wyzwalacz to **oscylacja followerów wokół progu `MaxPullGatherDistance`** podczas gdy tank jest w połowie drogi do moba.

Obie pętle znikną po wprowadzeniu zatrzasku (latch/commit) z dwuprogową histerezą i zasadą “dokończ rozpoczętą akcję przed zmianą celu”, co jest minimalną zmianą logiki wymaganą w zadaniu.

---

## Proponowany plan wdrożenia (kolejność)

1. **Najpierw Problem 1 (krytyczny dla walki):** zmodyfikuj `PositionBackline` → histereza 3 yd / 8 yd + latch na guid moba + warunek `if (me.Combat && target.InRangeAndLoS) skip reposition`. Przetestuj na logach Healer-a `Ksionc` — oczekiwany najszybszy efekt.

2. **Potem Problem 2 (tank):** dodaj `pullCommitted` w `PartyGrindBot` + debounce w `SquadCoordinatorService`. Przetestuj na powtórce trasy Mulgore z 3 postaciami (Paladin tank + Warlock + Priest) — mierz `Skipped` i liczbę unikalnych `Pull routine: Prairie Stalker` na sesję.

3. **Walidacja regresji:** upewnij się że `Looting`, `TownRun`, `CorpseRun`, `Provisioning` nie są dotknięte (logi `Looting` i `Waiting for squadmate` poza klastrami pull/backline muszą zostać).

---

*Koniec diagnozy. Plik przygotowany automatycznie na podstawie 10 logów i kodu z `Bots/PartyGrind/Logic/*.cs` oraz `PartyGrindBot.cs`.*
