# Instrukcje dla agentów

## Przegląd projektu

CopilotBuddy to desktopowy bot do World of Warcraft 3.3.5a (build 12340), przenoszący API i architekturę Honorbuddy na prywatne serwery WotLK. Aplikacja odczytuje i modyfikuje pamięć procesu gry, wykonuje Lua przez warstwę ASM/FASM, prowadzi zachowanie w synchronicznym drzewie zachowań i udostępnia interfejs WPF. Nawigacja korzysta z osobnej natywnej biblioteki `Navigation.dll` (Recast/Detour).

Najważniejsze moduły:
- `UI/` — aplikacja WPF, okna i wejście `UI/App.xaml` / `UI/App.xaml.cs`.
- `Styx/` — główne API i logika: zarządzanie botami, profilami, walką, ekwipunkiem, questami, nawigacją, pamięcią gry, wtyczkami, lokalizacją oraz IPC/PartyGrind.
- `Bots/` — botbase’y: battlegroundy, dungeony, questy, grind/LevelBot, zbieranie i PartyGrind/DiscoBot.
- `TreeSharp/`, `Buddy/`, `CommonBehaviors/` — kompozyty drzewa zachowań, coroutine’y, akcje i dekoratory wielokrotnego użytku.
- `GreenMagic/` — operacje pamięci, wstrzykiwanie/wykonywanie kodu i natywne interop.
- `Tripper/` — nawigacja i typy matematyczne; most do `Navigation.dll`.

## Stack i zależności

- C# / .NET 10 (`net10.0-windows7.0`), WPF i Windows Forms; kompilacja x86, `EnableWindowsTargeting=true`, nullable i implicit usings w głównym projekcie.
- SDK-style `CopilotBuddy.csproj`; rozwiązanie `CopilotBuddy.slnx` zawiera główną aplikację.
- Pakiety aplikacji: MahApps.Metro, Roslyn (`Microsoft.CodeAnalysis.CSharp` i `.Scripting`), System.Drawing.Common, komponenty System.* oraz WCF `System.ServiceModel.*`. Część bibliotek jest referencjami lokalnymi z `Lib/` (m.in. SQLite i FASM); `Navigation.dll` jest ładowana natywnie.
- Testy: xUnit 2, Microsoft.NET.Test.Sdk i xunit.runner.visualstudio.
- `Tools/DataBinImporter` ma osobny projekt .NET 10 x86 i MySqlConnector; `Tools/DataBinInspector` — osobny projekt .NET 10 x86.
- README deklaruje „C# 10”, ale `.csproj` nie ustawia `LangVersion`; dokładna wersja języka nie jest przypięta w konfiguracji.

## Struktura i typowe punkty wejścia

- `CopilotBuddy.csproj`, `CopilotBuddy.slnx`, `App.config`, `app.manifest` — konfiguracja i build aplikacji.
- `UI/App.xaml` / `UI/App.xaml.cs` — start UI i obsługa wyjątków; `UI/MainWindow.xaml.cs` — główne okno.
- `Styx/` i `Bots/` — główne obszary pracy nad logiką i botbase’ami. Każdy bot dziedziczy po `Styx.BotBase` i zwykle udostępnia `Root` typu `TreeSharp.Composite`.
- `Styx/Logic/Profiles/` — profile; `Styx/Logic/Pathing/` i `Tripper/Navigation/` — nawigacja; `Styx/Database/` — zapytania do lokalnych baz.
- `Tools/CopilotBuddy.PartyGrind.Tests/` — testy; pozostałe podprojekty `Tools/` to importer, inspektor, skrypty przygotowania PartyGrind i konwertery WRobot.
- `datadb/` — bazy gry i skrypty Python do walidacji/konwersji/napraw danych. `Lib/`, `Resources/`, `Themes/`, `Styx/Localization/` zawierają biblioteki, zasoby UI i lokalizację.
- `output/`, `bin/`, `obj/` to wyjście runtime/build (ignorowane); `output.zip` jest śledzonym pakietem runtime. Nie zakładaj, że zawartość outputu jest równoważna kodowi źródłowemu w repo.

## Komendy

Uruchamiaj z katalogu głównego; wymagany jest .NET SDK obsługujący .NET 10 i środowisko Windows do uruchomienia WPF oraz testów zależnych od Windows.

```powershell
dotnet restore CopilotBuddy.csproj
dotnet build CopilotBuddy.csproj -c Release /p:Platform=x86
dotnet test Tools/CopilotBuddy.PartyGrind.Tests/CopilotBuddy.PartyGrind.Tests.csproj -c Release /p:Platform=x86
dotnet run --project CopilotBuddy.csproj
```

Build głównej aplikacji jest też opisany w README jako `dotnet build CopilotBuddy.csproj -c Release`. Repo nie definiuje osobnego skryptu lintującego ani konfiguracji lintera. `Publish-Release.ps1` wykonuje build Release x86 i obfuskuje wynik przez `Obfuscar.GlobalTool` (`obfuscar.xml`); nie uruchamiaj go, jeśli zmiana nie wymaga publikacji. README opisuje odświeżenie archiwum runtime: `Compress-Archive -Path .\output\* -DestinationPath .\output.zip -Force` — wykonuj tylko, gdy faktycznie zaktualizowano runtime output.

## Konwencje kodu

- Zachowuj układ folderów i namespace odpowiadający modułowi. W kodzie występują zarówno starszy styl z klamrami namespace i tabulatorami, jak i nowsze pliki z 4 spacjami; dopasuj styl edytowanego pliku, zamiast formatować hurtowo.
- Typy i metody są zwykle w PascalCase, pola prywatne często w `_camelCase`; przestrzenie nazw odzwierciedlają katalogi. W nowych elementach uwzględniaj nullable annotations stosowane przez aktualny projekt.
- Botbase’y opierają się na `BotBase` i kompozytach `TreeSharp`; nie wprowadzaj asynchronicznego przepływu do pętli bota bez zrozumienia kontraktu tick/pulse.
- Błędy obsługuj lokalnie zgodnie z sąsiednim kodem; logowanie aplikacji prowadzi przez `Styx.Helpers.Logging` (`WriteDebug`, `WriteException` i pokrewne). Istnieją dedykowane typy wyjątków dla wielu błędów API.
- `CONTRIBUTING.md` zabrania dodawania komentarzy (zachowuje się komentarze oryginalnego HB). Przestrzegaj tej reguły, chyba że projekt wyraźnie zmieni wytyczne.
- To port Honorbuddy: zmiany zachowania powinny odpowiadać wzorcowi oryginału opisanemu w `CONTRIBUTING.md`. W PR należy wskazać wersję HB/metodę źródłową, gdy dotyczy.

## Testy

Testy xUnit są w `Tools/CopilotBuddy.PartyGrind.Tests/`, w plikach `*Tests.cs`; większość dotyczy logiki koordynacji i IPC PartyGrind. Pisz przypadki jako `[Fact]` z nazwą opisującą scenariusz i oczekiwany efekt, używaj asercji xUnit. Testy mogą korzystać z helperów lokalnych i tymczasowych katalogów; sprzątaj stan statyczny i pliki w `Dispose`.

Przed zmianą logiki uruchom testy projektu wskazaną komendą, a następnie zbuduj aplikację. Dla zmian zachowania gry, nawigacji, pamięci lub UI testy jednostkowe nie zastępują testu w kliencie WoW 3.3.5a — `CONTRIBUTING.md` wymaga testu w grze i logu dla PR. Brak skonfigurowanego testowego lint/build workflow CI w znalezionych plikach.

## Zasady bezpiecznej pracy

- Utrzymuj zmianę małą i ograniczoną do wymaganej funkcji; przed edycją sprawdź istniejące wywołania i analogiczne implementacje.
- Publiczne API może być używane przez boty, pluginy, profile, rutyny i dungeon scripts kompilowane w runtime. Po zmianie publicznego kontraktu sprawdź wszystkich dostępnych callerów; nie zakładaj, że kompilacja głównego projektu wykryje błędy zawartości runtime.
- Nie edytuj bez potrzeby offsetów, struktur pamięci, baz danych/binarnych plików, DLL w `Lib/`, zasobów wygenerowanych, `output.zip` ani konfiguracji release. Zmiany offsetów lub memory layout mogą unieruchomić klienta gry.
- Nie dodawaj sekretów, danych kont ani konfiguracji lokalnej. W repo nie znaleziono standardowego pliku `.env` ani manifestu sekretów; zachowaj ostrożność z danymi i plikami zewnętrznymi.
- Nie generuj artefaktów do repo: `bin/`, `obj/`, logi i `output/` są ignorowane. Nie przebudowuj `output.zip`, jeśli zmiana nie dotyczy wydawanego runtime.
- Po pracy sprawdź `git diff`, uruchom testy i build, jeśli dostępne; jasno zgłoś, czego nie udało się wykonać. Do testów funkcjonalnych wymagających gry potrzebny jest odpowiedni klient i środowisko Windows.
