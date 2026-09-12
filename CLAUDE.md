# LoupixDeck.Plugin.Audio

LoupixDeck-Plugin. Erstellt mit dem `create-loupix-plugin` Skill.

## Referenz-Verzeichnisse

Diese Ordner liegen lokal und enthalten alles, was zum Verständnis und zur Entwicklung dieses Plugins nötig ist:

- **Wiki (Plugin-SDK-Dokumentation):** `C:\!Code\LoupixDeck.PluginSdk.wiki`
  Erste Anlaufstelle für SDK-Konzepte, Lifecycle, Manifest, Commands, Settings, Folder-Provider.
- **SDK (Quellcode + lokales NuGet-Feed):** `C:\!Code\LoupixDeck.PluginSdk`
  Enthält die Basisklassen (`LoupixPlugin`), Interfaces (`IPluginCommand`, `IPluginHost`, `IDisplayCommand`, `IFolderProvider`, `IPluginSettingsPage`) und unter `nupkg\` das NuGet-Paket, gegen das hier gebaut wird (siehe `nuget.config`).
- **Host-Software (LoupixDeck):** `C:\!Code\LoupixDeck`
  Lädt das Plugin zur Laufzeit. Hier liegt der `PluginManager` und die Logik für Plugin-Discovery, Manifest-Parsing und Command-Ausführung.
- **Referenz-Plugin (vollständiges Beispiel):** `C:\!Code\LoupixDeck.Plugin.Audio`
  Funktionierendes Plugin mit Commands, Folder-Providern, Settings-Page und Plattform-spezifischen Services. Als Vorlage für komplexere Features verwenden.

## Plugin-Grundgerüst

- **Assembly-/Ordnername:** `LoupixDeck.Plugin.Audio` (Konvention: `LoupixDeck.Plugin.<Name>`)
- **Namespace:** `LoupixDeck.Plugin.Audio`
- **Plugin-Klasse:** `AudioPlugin` erbt von `LoupixPlugin`
- **Manifest:** `plugin.json` (id = `spotifypremium`, sdkVersion = `1.4`, entryAssembly = `LoupixDeck.Plugin.SpotifyPremium.dll`)
- **Target Framework:** `net9.0`
- **SDK-Paket:** `LoupixDeck.PluginSdk` 1.4.0 mit `<ExcludeAssets>runtime</ExcludeAssets>` — der Host stellt die SDK-DLL bereit, nie mit ausliefern.

## Build & Deploy

```bash
dotnet build -c Release
```

Output landet in `bin\Release\` (kein TFM-Suffix wegen `AppendTargetFrameworkToOutputPath=false`). Zum Testen den Inhalt nach `<LoupixDeck>\plugins\spotifypremium\` kopieren — `plugin.json` muss dort neben der DLL liegen.

## Pflicht-Member von `LoupixPlugin`

- `Metadata` — `PluginMetadata` mit `Id`, `Name`, `Version`, `SdkVersion`, optional `Author`/`Description`/`Icon`.
- `Initialize(IPluginHost host)` — einmaliger Setup-Hook; Host für Logging (`host.Logger`), Settings (`host.Settings`), Command-Ausführung und Button-Refresh nutzen.
- `GetCommands()` — `IEnumerable<IPluginCommand>` aller Commands.
- `Shutdown()` (optional überschreiben) — Ressourcen freigeben.

## Commands schreiben

Jeder Command implementiert `IPluginCommand`:

- `Descriptor` — `CommandName` (stabile öffentliche ID, Konvention: `SpotifyPremium.<Feature>`), `DisplayName`, `Group` (= `"SpotifyPremium"`).
- `SupportedTargets` — `ButtonTargets.All` oder einschränken.
- `Execute(CommandContext ctx)` — gibt `Task` zurück, läuft im Background-Thread, MUSS `try/catch` umschließen, darf nicht blockieren.

Für dynamisch beschriftete Buttons zusätzlich `IDisplayCommand` implementieren. Für Touchscreen-Ordner `IFolderProvider`. Für Settings-UI `IPluginSettingsPage`. Konkrete Patterns: siehe Referenz-Plugin `LoupixDeck.Plugin.Audio`.

## Wichtige Regeln

1. **Nicht** die SDK-DLL mit dem Plugin ausliefern (`<ExcludeAssets>runtime</ExcludeAssets>` ist gesetzt).
2. **Genau eine** konkrete `LoupixPlugin`-Subklasse pro Assembly — der Host findet sie per Reflection.
3. `CommandName` ist eine **stabile öffentliche API** — nach Release nicht mehr umbenennen.
4. `Metadata.Id` (lowercase), `plugin.json#id` und der Plugin-Ordnername unter `plugins\` müssen identisch sein.
5. `Execute` läuft **nicht** auf dem UI-Thread — keine Avalonia-Objekte direkt anfassen.

## Parametrisierte Commands (IMenuContributor)

Wenn ein Command Parameter aus einem `MenuNode` empfangen soll (z. B. Geräte-ID, Playlist-ID), müssen **drei** Felder am `CommandDescriptor` gesetzt sein — sonst landet der Parameter-Wert **nicht** in der gespeicherten Button-Belegung und `CommandContext.Parameters` ist beim Execute leer:

```csharp
public CommandDescriptor Descriptor { get; } = new()
{
    CommandName     = "Audio.VolumeUp",
    DisplayName     = "Audio: Volume Up",
    Group           = "Audio",
    HiddenFromMenu  = true,                                    // sonst doppelt im Menü
    ParameterTemplate = "({deviceId})",                        // PFLICHT, sonst geht der Wert verloren
    Parameters      = [new CommandParameter("deviceId", typeof(string))],
};
```

- **`ParameterTemplate`** ist das Format, mit dem der Host den Binding-String baut (siehe `LoupixDeck\Services\CommandBuilder.cs:BuildCommandString`). Er ersetzt `{paramName}` durch den Wert aus `MenuNode.Parameters`. Fehlt das Template, bleibt nur der nackte Command-Name in der Belegung.
- Die **Parameter-Namen** in `ParameterTemplate`, im `CommandParameter`-Schema und in den `MenuNode.Parameters`-Keys müssen exakt übereinstimmen (case-sensitive).
- Im `IMenuContributor` als Wert immer die **stabile, eindeutige ID** (z. B. WASAPI-GUID, `sink:`-Name, Playlist-ID) verwenden, nie den Anzeigenamen — der Anzeigename gehört in `MenuNode.Name` und kann sich durch Aliase oder OS-Updates ändern, ohne dass bestehende Bindings brechen.
- Funktionierende Vorlage: `LoupixDeck.Plugin.SpotifyPremium\Commands\Playlists\PlaylistCommands.cs` und `LibraryCommands.cs`.

## Mixer und Default-Device: zwei Mechanismen, die man dem Code nicht ansieht

- **`PolicyConfig.cs` kapselt eine undokumentierte COM-Schnittstelle.** Windows bietet keinen
  unterstützten Weg, das Standard-Audiogerät zu wechseln; `IPolicyConfig` ist der Weg, den alle
  Werkzeuge gehen. Die GUIDs sind seit Windows 7 stabil, aber nichts garantiert das für das
  nächste Release. Deshalb liegt alles davon in genau einer Datei und **jeder Fehler bleibt
  nicht-fatal** — `SetDefaultEndpoint` gibt `false` zurück, der Aufrufer macht nichts.
- **Eine Anwendung wird über den Prozessnamen adressiert, nicht über die Session-ID.** Session-IDs
  und PIDs ändern sich bei jedem Neustart der Anwendung und würden gespeicherte Button-Belegungen
  über Nacht still unbrauchbar machen. Die `AppId` ist der Name der ausführbaren Datei,
  kleingeschrieben und ohne Endung (`chrome`, `spotify`). Eine Aktion gilt damit für **alle**
  Sessions dieses Prozesses, was bei einem Browser mit einer Session pro Tab auch das erwartete
  Verhalten ist.
- **Ohne `endpointId` werden ALLE aktiven Render-Endpoints durchlaufen, nicht nur das
  Standardgerät.** WASAPI hängt Sessions am Gerät, und Anwendungen spielen nicht alle auf dem
  Standardgerät: ein virtueller Mixer wie SteelSeries Sonar gibt jeder Anwendung ein eigenes
  Gerät. Ein Mixer, der nur das Standardgerät abfragt, findet MPC-HC oder den Browser schlicht
  nicht, obwohl sie hörbar laufen. Gemessen auf einem Sonar-System: 8 Endpoints, die gesuchte
  Anwendung lag auf "Sonar - Media", das Standardgerät war "Sonar - Gaming". Ein explizit
  übergebenes `endpointId` schränkt weiterhin auf genau dieses Gerät ein.
- **Drei Caches in `WindowsAudioService`, alle gegen dieselbe NAudio-Eigenheit.** NAudio gibt
  mehrere COM-Objekte nur über den GC frei, nie deterministisch. Ohne Cache klettert die
  Handle-Zahl bei laufendem Mixer (Abfrage alle 750 ms) sichtbar:
  - `AudioSessionManager` / `SessionCollection` haben kein `Dispose`. Pro Aufruf ein neues
    `MMDevice` samt Manager kostete **202 Handles auf 200 Aufrufe**. Darum ein gecachtes Gerät
    pro Endpoint plus `RefreshSessions()` — das erkennt neu gestartete Anwendungen trotzdem
    (gemessen ~1 s).
  - `EnumerateAudioEndPoints` verliert **pro Aufruf ein Handle je Endpoint**, unabhängig davon,
    ob man die zurückgegebenen `MMDevice` disposed. Darum wird die Endpoint-Liste 5 Sekunden
    lang wiederverwendet (`EndpointListLifetime`); ein neu angestecktes Gerät taucht entsprechend
    binnen 5 s auf.
  - `Process.GetProcessById` kostet ~2 ms pro PID. Bei ~30 Sessions über 8 Endpoints dominierte
    das die Abfrage. Der Name kommt jetzt aus `QueryFullProcessImageName` (~20× billiger) mit dem
    verwalteten Aufruf als Fallback, zusätzlich pro PID gecacht und aufgeräumt, sobald die
    Session weg ist.

  Zusammen: **2 Handles auf 2000 Abfragen, ~2,5 ms pro Abfrage über 8 Endpoints.** Ein Cache
  wird verworfen, wenn das Gerät verschwindet oder die Enumeration fehlschlägt;
  `WindowsAudioService` ist dafür `IDisposable` und wird vom Plugin beim Shutdown freigegeben.
- **Zwei Sessions erscheinen nie als Anwendung.** `audiodg` ist die Windows-Audio-Engine und
  taucht auf jedem Endpoint auf, durch den ein virtuelles Gerät schleift — Klempnerei, kein
  Programm, das jemand leiser drehen will. Die System-Sounds-Session hat gar keinen eigenen
  Prozess (PID 0, `Process.GetProcessById` liefert "Idle"); sie wird über
  `IsSystemSoundsSession` erkannt und heißt `system` / "System Sounds". Anzeigenamen, die mit
  `@` beginnen, sind nicht aufgelöste Ressourcen-Verweise wie
  `@%SystemRoot%\System32\AudioSrv.Dll,-202` und werden durch die `AppId` ersetzt.
