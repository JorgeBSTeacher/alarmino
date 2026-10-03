# Alarmino — Arquitectura y testing (guía de revisión)

> Documento técnico para un programador que revise el repositorio.
> Complementa a `SSOT.md`, que recoge requisitos, decisiones y mejoras.

## 1. Solución y proyectos

`Alarmino.sln` agrupa la app y las herramientas en dos carpetas:

```
├── src/
│   ├── Alarmino.Core        → net8.0 (biblioteca sin dependencias de UI)
│   ├── Alarmino             → net8.0-windows (WPF + WinForms para bandeja)
│   └── Alarmino.Tests       → net8.0 (xUnit)
├── tools/
│   ├── Alarmino.Bench       → net8.0 (banco pequeño de pruebas de rendimiento)
│   └── Alarmino.Installer   → net8.0-windows (WPF, instalador self-extracting)
└── scripts/                 → build/test/publish/package (bash y PowerShell)
```

### 1.1 `Alarmino.Core`
Núcleo de dominio, **sin referencias a WPF**: se ejecuta y se testea en Linux.

| Área | Clases |
|---|---|
| Modelos | `Alarm`, `SoundSelection`, `AppSettings`, `PlaybackMode`, `SoundSourceKind` (en `Models/`) |
| Metadatos de presets | `SoundPreset` / `SoundPresets` (`airplaneBeep`, `softChime`, `fireAlarm`) + `MigratePresetId` (ids antiguos `bell`/`ring`/`schoolTone`) |
| Audios predeterminados | `PresetsDefault/*.mp3` **embebidos** en el ensamblado; `SoundLibrary.EnsureDefaultPresets()` los copia a `sounds\presets` |
| Programación | `SchedulerEvaluator`, `ITimeProvider` / `SystemTimeProvider` / `FakeTimeProvider` |
| Persistencia | `JsonDataStore` (JSON atómico en `%APPDATA%\Alarmino`) |
| Audio generado | `PresetSynthesizer`, `FadeOutWaveStream` (NAudio) |
| Sonidos de usuario | `SoundLibrary` (biblioteca `%APPDATA%\Alarmino\sounds`, `EnsureDefaultPresets`) |
| Descargas de audio | `TrackSanity` (descarta descargas vacías, truncadas o corruptas sin depender del códec) |
| Sonido de YouTube | `YouTubeAudioResolver` (YoutubeExplode: lista la playlist, elige pista al azar, **precarga** la descarga), `YouTubeTrackCache` (pistas ya descargadas, una por playlist, máx. 3, recuperadas entre reinicios), `ConnectivityProbe` (control de conexión, cacheado 5 s), `IYouTubeAudioResolver` |
| Planos de reproducción | `PlaybackRequest` |

### 1.2 `Alarmino` (app WPF)
Capa de presentación y servicios de OS (solo Windows):

- **ViewModels** (MVVM, `CommunityToolkit.Mvvm` con source generators): `MainViewModel` (lista, scheduler, bandeja, configuración) y `AlarmEditorViewModel` (alta/edición + previsualización).
- **Views**: `MainWindow`, `EditorWindow`, `SettingsWindow` (XAML + code-behind mínimos: diálogo, cierres).
- **Services**:
  - `AudioService` — reproducción NAudio (`WaveOutEvent`), modo completo/duración personalizada, repetición con pausa, parada inmediata, cola por lock.
  - `TruncatedWaveStream` — recorta a una duración máxima.
  - `TrayIconService` — `NotifyIcon` (bandeja), menú contextual, globos de notificación.
  - `AutostartService` — autoejecución en `HKCU\...\Run` (`/min` para arrancar a bandeja).
  - `ThemeService` — tema claro/oscuro en vivo (ver §5).
- **Theme/**: `Dark.xaml`, `Light.xaml`, `Controls.xaml` (estilos implícitos).

### 1.3 `Alarmino.Installer`
Programa GUI WPF que hace de instalador y desinstalador:

- `Program` — modos `/S` (silencioso), `/Uninstall`, `/D=dir`; **detecta por nombre de proceso** (`UninstallAlarmino`) el modo desinstalar y lo propaga a `InstallerWindow`.
- `InstallerEngine` — instalación (extraer, accesos directos, autostart, registro de desinstalación, copia como `UninstallAlarmino.exe`) y desinstalación (accesos directos, autostart y clave HKCU; borrado de datos opcional).
- `SelfExtractor` (+`BoundedStream`) — extrae el `.zip` adjunto al final del propio exe.
- `ShortcutHelper` — accesos directos `.lnk` vía `WScript.Shell` COM, autostart, clave `Uninstall`.

Formato del instalador: `[stub.exe][payload.zip]ALARMINO_PAYLOAD[longitud u64 little-endian]`. El payload se compone **sin Windows** (`scripts/compose-installer.py`): stub + zip + marca.

## 2. Flujos principales

### 2.1 Disparo de alarmas
- `DispatcherTimer` de 500 ms en `MainViewModel` → `SchedulerEvaluator.Evaluate(now, alarms)`.
- `SchedulerEvaluator` es **lógica pura** con un reloj inyectable. Solo dispara «entrando» en el minuto de una alarma:
  - Mismo minuto → `ToPlay` (suena el toque).
  - Minutos intermedios saltados (suspensión/apagado) → `Missed` (se registra «Omitida» en `events.json` y opcionalmente notifica; **no suena retroactivamente**).
- `PlaybackRequest` se construye con modo, duración, repeticiones y volumen y se entrega a `AudioService`.

### 2.2 Reproducción de audio
- `AudioService.PlayOnce`: preset, archivo o YouTube → `WaveStream`.
  - Preset: orden de resolución = **override del usuario** (`PresetOverride.FilePath`) → archivo de `sounds\presets\{id}.mp3` (los tres que incluye la app, extraídos del recurso embebido al arrancar) → `PresetSynthesizer.Create` como último recurso (PCM 16-bit en `RawSourceWaveStream`, por si la instalación quedó sin audios).
  - Duración personalizada: `TruncatedWaveStream` + `FadeOutWaveStream` (fade lineal de 5 s al final).
  - **YouTube** (`SoundSourceKind.YouTube`): `PlayAsync` resuelve **una sola vez por disparo** (antes del bucle de repeticiones) la pista con `YouTubeAudioResolver`; el mismo archivo local se reutiliza en todas las repeticiones.
    - `ResolveAsync` **no descarga nada en el caso normal**: toma la pista precargada de `YouTubeTrackCache` (`TryTake`, sin red ni espera) y, al terminar, encola en segundo plano la precarga de la siguiente (`QueueRefill`).
    - Si no hay pista precargada: `ConnectivityProbe` (`generate_204`, timeout 4 s) y descarga en el momento. Cada descarga se valida con `TrackSanity.LooksPlayable` comparando el tamaño real con el que anuncia el manifiesto (`IStreamInfo.Size`, margen del 2 %), con un suelo de 4 KB; si el manifiesto no da tamaño, se estimationa la duración por la cabecera MP3 (≥ 1 s). Si no pasa, se borra y se trata como fallo → **MP3 de respaldo**. No penaliza los sonidos cortos: un bip de 2 s se acepta tal cual. Si no hay red o falla, devuelve el **MP3 de respaldo** (`AppSettings.OfflineFallbackFilePath`, con reenvío a `resolveFallbackFile`); sin respaldo, no suena.
    - Precarga (`PrefetchAsync`): disparada por `MainViewModel` al arrancar, al guardar/cambiar alarmas o ajustes y en una barredura cada 30 min. Se salta si la pista es reciente (`MaxTrackAge` 6 h) o si ya hubo un intento hace menos de 30 min (`MinPrefetchInterval`), y nunca solapa descargas (`SemaphoreSlim` no bloqueante). `YouTubeTrackCache` guarda una pista por playlist (`{playlistId}--{videoId}.ext`, hasta 3), borra huérfanos y los archivos en descarga (`BeginWrite`), y `LoadExisting` recupera las pistas de sesiones anteriores para que el arranque no obligue a descargar.
    - La pista AAC (`.m4a`) se reproduce con `MediaFoundationReader`.
- `WaveOutEvent.Init(source)`, volumen global, `PlaybackStopped` libera recursos. `Stop()` corta al instante (null-safe con `_gate`).

### 2.3 Configuración y notificaciones
- Tema: `ThemeService.Apply` **reemplaza el diccionario de tema** en las `MergedDictionaries` de `App.xaml` **y** re-publica los pinceles en `Application.Resources` (doble mecanismo). Las tres ventanas declaran `Background/Foreground` explícitos con `DynamicResource`.
- Notificaciones de Windows: la opción «Mostrar notificaciones…» (propiedad `NotificationSoundEnabled`) **gating total**: con ella desactivada no se muestra ningún globo (ni en alarma tocada ni en omitida). Con ella activa, `TrayIconService.ShowNotification` muestra globo con sonido (`ToolTipIcon.Info`).

### 2.4 Autostart y bandeja
- `AutostartService` escribe `"ruta\Alarmino.exe" /min` (o sin `/min` si «mostrar ventana al inicio») en `HKCU\...\Run`.
- Al cerrar la ventana → se oculta a la bandeja; «Salir» del menú de bandeja termina el proceso.

## 3. Persistencia y biblioteca de sonidos

- `%APPDATA%\Alarmino\`: `alarms.json`, `config.json`, `events.json`, `logs\alarmino-AAAA-MM-DD.log`, `sounds\`, `cache\youtube\` (pista de YouTube en curso, se limpia al resolver otra).
- `JsonDataStore`: escritura **atómica** (`.tmp` + rename/replace). Al `LoadAlarms()`/`LoadSettings()` **migra** los sonidos de archivo que apunten fuera de la biblioteca: los copia a `sounds\` (nombre `guid_archivo.ext`) y actualiza la referencia.
- `SoundLibrary`: `StoreUserSound` (copia con nombre único; no duplica si ya está en la biblioteca), `ResolvePresetFile`, `Normalize`.

## 4. Empaquetado y scripts

| Script | Qué hace |
|---|---|
| `scripts/build.sh` / `build.ps1` | `dotnet build Alarmino.sln -c Release` |
| `scripts/test.sh` / `test.ps1` | `dotnet test` |
| `scripts/publish.sh` / `publish.ps1` | `dotnet publish` self-contained win-x64 a `artifacts/win-x64` |
| `scripts/package.sh` / `package.ps1` | publish + stub del instalador + `compose-installer.py` → `artifacts/AlarminoSetup.exe` |
| `scripts/compose-installer.py` | compone `[stub][zip]ALARMINO_PAYLOAD[u64]` |
| `scripts/analyse.sh`, `bench.sh`, `dev.sh`, `gen-icon.py`, `smoke-win.ps1` | apoyo |

Cross-compilation: la app e instalador usan `EnableWindowsTargeting`, así que **todo se compila en Linux**; la parte visual/audio se ejecuta/proteba en Windows.

## 5. Testing

### 5.1 Estrategia
La lógica que importa vive en `Alarmino.Core` (net8.0), por lo que la **mayoría del testeo se ejecuta en CI/Linux** sin WPF ni dispositivo de audio. La UI y la salida real de sonido se validan manualmente en Windows (limitación conocida).

### 5.2 Casos (66 tests, xUnit verde)
| Archivo / clase | Qué cubre |
|---|---|
| `SchedulerEvaluatorTests` (10) | disparo al entrar en el minuto, no redispare dentro del mismo minuto, días de la semana, alarmas inactivas, **franjas omitidas por salto de tiempo** (equipo dormido), orden de coincidencias — con `FakeTimeProvider` |
| `PresetSynthesizerTests` (9) | cada preset produce señal no vacía, duración esperada y **RMS audible** (> 0.05) — red de seguridad si falta un audio |
| `FadeOutWaveStreamTests` (3) | la señal se mantiene antes del fade, llega **a ~silencio** al final y decae en la ventana de fade |
| `SoundLibraryTests` (4) | copia a biblioteca con nombre único, `Normalize` (migración), resolución de preset y **`EnsureDefaultPresets`**: instala los 3 MP3 y comprueba que son MPEG válidos con duración > 1 s |
| `TrackSanityTests` (5) | acepta un MP3 real y un **bip de 2 s** completo, rechaza vacío/mínimo/inexistente, detecta una descarga **truncada al 60 %** comparando con el tamaño del manifiesto, y sin tamaño esperado decide por la duración de la cabecera (y `null` si no es MP3) |
| `YouTubeTrackCacheTests` (8) | tomar pista una sola vez, caducidad/frescura, sustitución por playlist, tope de 3 pistas, recuperación entre sesiones, borrado de huérfanos y protección del archivo en descarga |
| `ModelsAndStoreTests` → `DayFlagsTests` (4) + `JsonDataStoreTests` (7) + `SoundPresetsTests` (4) | flags y texto de días, **round-trip JSON** de alarmas/config/eventos, archivos ausentes → valores por defecto, **round-trip de `SoundSelection.YouTube`** y del MP3 de respaldo, los 3 sonidos predeterminados y su **migración de ids antiguos** |
| `ModelsAndStoreTests` → `YouTubeAudioResolverTests` (12) | extracción del id de playlist (URLs válidas/inválidas), **decisión offline → MP3 de respaldo** (con `IConnectivityProbe` falso) y **uso de la pista precargada sin tocar la red** |

### 5.3 Ejecución
```bash
dotnet build Alarmino.sln -c Release
dotnet test Alarmino.sln -c Release
dotnet format Alarmino.sln --verify-no-changes --no-restore
```

### 5.4 CI (GitHub Actions, `build.yml`)
`dotnet build` (Debug) → `dotnet test` (cobertura xPlat) → `dotnet format --verify-no-changes`.
El instalador se compone en un job de Windows (`scripts/package.ps1`).

### 5.5 Huecos conocidos
- UI WPF y reproducción real de audio (prueba manual en Windows).
- **Descarga y reproducción real de YouTube** (solo se testea la lógica de decisión con conectividad falsa; la red real y los streams de YouTube se validan en Windows).
- Ciclo de instalación/desinstalación real (prueba manual; solo scripts de apoyo como `smoke-win.ps1`).
- Comportamiento con Windows suspendido (solo cubierto a nivel de scheduler con el reloj falso).