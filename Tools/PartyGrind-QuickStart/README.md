# Party Grind — szybki start (3–5 klientów)

Ten zestaw przygotowuje konfigurację i generuje launcher dla botbazy **„Party Grind”**.
Skrypt wiąże każdego bota z konkretnym procesem WoW przez **`-pid`** — nie przez tytuł okna.
Tytuł okna jest tylko podpowiedzią przy wyborze PID-a.

## 1. Warunki wstępne

- WoW 3.3.5a, **build 12340**, klient x86.
- Katalog z botem (dalej `BOT_DIR`) zawiera `CopilotBuddy.exe`, `Navigation.dll`,
  `data.bin`, `Spells.bin` oraz folder `mmaps/`.
- 3 do 5 otwartych klientów WoW, zalogowanych na postacie z tej samej frakcji i w tym samym
  obszarze, który obsługuje wybrany profil.
- Profil grindowy HBProfile z sekcją `GrindArea/Hotspots` (nie profil questowy).
  Przykład z paczki: `Default Profiles\test\Grind\A-H grind 1-60\Alternate Files(test-demo)\[1-12] Tauren Start_HB.xml`.
  Unikaj profili Cataclysm — bot celuje w 3.3.5a.
- PowerShell 5.1 lub nowszy (Windows).

## 2. Uruchom setup raz

W PowerShell, z katalogu repo:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\PartyGrind-QuickStart\Setup-PartyGrind.ps1 `
  -BotDir "C:\CopilotBuddy" `
  -ProfilePath "C:\CopilotBuddy\Default Profiles\test\Grind\A-H grind 1-60\Alternate Files(test-demo)\[1-12] Tauren Start_HB.xml" `
  -CharacterNames TankChar,HealerChar,DpsChar `
  -Roles TankLeader,Healer,DpsMelee `
  -TeamId Squad1 `
  -IpcPort 1339
```

Możesz też zamknąć całą listę w cudzysłów — skrypt sam rozdziela przecinki:
`-CharacterNames "TankChar,HealerChar,DpsChar" -Roles "TankLeader,Healer,DpsMelee"`.

Dla 4 lub 5 klientów dodaj kolejne nazwy i role, np.
`-ClientCount 4 -CharacterNames A,B,C,D -Roles TankLeader,Healer,DpsMelee,DpsRanged`.

Dozwolone role: `TankLeader`, `Healer`, `DpsMelee`, `DpsRanged`, `HybridOffHeal`.
Pierwsza postać **musi** być `TankLeader` — dostaje `Is Coordinator=true` i hostuje IPC.

Setup tworzy w `BOT_DIR`:

| Plik | Zawartość |
| --- | --- |
| `Secrets\PartyGrindSecrets.json` | losowe sekrety 32 bajty na postać |
| `Settings\Instances\ClientN\SquadSettings_<Postać>.xml` | ścieżka do sekretów dla każdego instance |
| `Start-PartyGrind.bat` | gotowy launcher z nazwami, rolami, profilem i `-pid` |

Jeśli zmieniasz nazwy/postacie lub chcesz wygenerować nowe sekrety, dodaj `-Force`.
Uwaga: `-Force` nadpisuje istniejący `PartyGrindSecrets.json` i ustawienia instancji.

## 3. Uruchom test

1. Uruchom klientów WoW i zaloguj się na postacie z listy.
2. Uruchom `BOT_DIR\Start-PartyGrind.bat`.
3. Skrypt pokaże tabelę procesów `Wow` z `Id`, `StartTime` i `MainWindowTitle`.
   Wpisz PID odpowiadający każdej postaci. Tytuł okna służy tylko do rozpoznania —
   bot wiąże się z wpisanym PID-em (`-pid=%PID1%` itd.).
4. Launcher odpala najpierw koordynatora, potem (po 3 s) followerów.
5. Po starcie sprawdź logi:
   - `[PartyGrind] Started as ... (Coordinator=..., Team=..., Port=...)`,
   - komunikaty `[PartyGrind IPC]` o połączeniu,
   - w overlayzie liczbę connected members.
6. Koordynator powinien automatycznie zaprosić resztę do grupy (`AutoInviteMembers=true`).

## 4. Co dokładnie ustawić w UI

CLI z launchera nadpisuje przy każdym starcie: rolę, `IsCoordinator`, Team ID, port,
listę członków i nazwę koordynatora. W config window zmieniaj **pozostałe** opcje.
Ścieżka sekretów jest już zapisana przez setup — nie wpisuj sekretów do bat-a.

### Zakładka „Squad & Role”

| Ustawienie | Wartość dla pierwszego testu |
| --- | --- |
| Team ID | `Squad1` (identyczne u wszystkich; nadpisuje CLI) |
| IPC Loopback Port | `1339` (identyczne u wszystkich; nadpisuje CLI) |
| Squad Role | Client1: `TankLeader`, Client2: `Healer`, Client3: `DpsMelee` |
| Is Coordinator | zaznaczone tylko na Client1 |
| Coordinator Character Name | dokładna nazwa postaci Client1 |
| Expected Roster (CSV) | `TankChar,HealerChar,DpsChar` (bez spacji) |
| IPC Secrets File | ścieżka do `Secrets\PartyGrindSecrets.json` (ustawiona przez setup) |
| Coordinator auto-invites | ON, interwał `10` s |
| Auto-accept invite | ON |
| Strict invite whitelist | ON |
| Auto-load Coordinator profile | ON |
| Level Advance Mode | `WaitForLowestMember` |
| Disconnect Grace | `120` s |

### Zakładka „Combat & Formation”

Wartości domyślne są rozsądne na start: DPS delay `700` ms, Healer assist powyżej `70%`
many, emergency self-heal ON, tank stance/aura ON. Formacja: Soft `35`, Resume `20`,
Hard backtrack `65`, Max pull `28`, Ranged standoff `21`, Healer standoff `22`,
Melee flank `3.2`, Mount policy `SyncWithSlowestMember`. Rest: HP `50/85`, Mana `35/80`.

### Zakładka „Loot, Town & Provisioning”

| Ustawienie | Wartość | Uwaga |
| --- | --- | --- |
| Loot Method | `FreeForAll` | `GroupLoot` wymaga poprawnego roll rule |
| Group Roll Rule | `Greed` / `SmartUpgrade` | `SmartUpgrade` potrzebuje danych o itemach |
| Max Loot Window | `6` s | |
| Synchronize TownRuns | ON | dla pierwszego krótkiego testu możesz wyłączyć, żeby nie wjeżdżać do miasta |
| Trigger Free Bag Slots | `2` | |
| Trigger Durability | `20` % | |
| Visit Class Trainers | ON | wyłącz na czas smoke testu, jeśli nie chcesz testować miasta |
| Wait for Party Ress | `45` s | |
| Mage food & water | ON tylko gdy w squadzie jest mag | inaczej wyłącz |
| Prefer Mage over vendor | ON | |
| Min/Target food i drink | `10 / 20` | |
| Trade timeout / cooldown | `12 s / 30 s` | |
| Warlock healthstones | ON tylko gdy jest warlock | |
| Ritual tables / cross buffs / markers | ON | |

Zapisz zmiany przyciskiem **Save & Close** w każdej instancji, której dotyczy zmiana.

## 5. Najczęstsze problemy

- **`No IPC secret configured for this character.`** — nazwa postaci w grze/UI
  różni się od klucza w `PartyGrindSecrets.json`. Uruchom setup ponownie z poprawnymi
  `-CharacterNames` i `-Force`.
- **`IPC secret ... must decode to at least 32 bytes`** — stary/uszkodzony plik sekretów;
  `Setup-PartyGrind.ps1` generuje poprawne 32-bajtowe wartości.
- **`[PartyGrind IPC] Startup failed`** — sprawdź port (`1339`), zaporę, czy koordynator
  wystartował pierwszy i czy wszystkie instancje mają ten sam `TeamId`.
- **Bot nie dołącza do właściwego WoW** — wpisz poprawny PID z tabeli procesów.
  `-instance=ClientN` izoluje ustawienia, ale attachment idzie po `-pid`.
- **„Started as ... RequiresProfile” / brak hotspotów** — użyj profilu grindowego
  z `GrindArea/Hotspots`, nie questowego.
- **Błędy nawigacji** — sprawdź folder `mmaps/` przy `CopilotBuddy.exe`
  (format 4x4, Trinity/MMAP v5).
- **Mag nie zaopatruje w wodę** — postać 4. poziomu nie ma ancora jedzenia,
  ale ma `Rank 1` wody; to oczekiwane zachowanie.
