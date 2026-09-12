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
| Metadatos de presets | `SoundPreset` / `SoundPresets` (`bell`, `ring`, `schoolTone`) |
| Programación | `SchedulerEvaluator`, `ITimeProvider` / `SystemTimeProvider` / `FakeTimeProvider` |
| Persistencia | `JsonDataStore` (JSON atómico en `%APPDATA%\Alarmino`) |
| Audio generado | `PresetSynthesizer`, `FadeOutWaveStream` (NAudio) |
| Sonidos de usuario | `SoundLibrary` (biblioteca `%APPDATA%\Alarmino\sounds`) |
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
- `AudioService.PlayOnce`: preset o archivo → `WaveStream`.
  - Preset: busca primero `sounds\presets\{id}.wav|mp3` (integración futura de archivos del usuario); si no existe, **sintetiza** `PresetSynthesizer.Create` → PCM 16-bit en `RawSourceWaveStream` (sin cabecera WAV).
  - Duración personalizada: `TruncatedWaveStream` + `FadeOutWaveStream` (fade lineal de 5 s al final).
- `WaveOutEvent.Init(source)`, volumen global, `PlaybackStopped` libera recursos. `Stop()` corta al instante (null-safe con `_gate`).

### 2.3 Configuración y notificaciones
- Tema: `ThemeService.Apply` **reemplaza el diccionario de tema** en las `MergedDictionaries` de `App.xaml` **y** re-publica los pinceles en `Application.Resources` (doble mecanismo). Las tres ventanas declaran `Background/Foreground` explícitos con `DynamicResource`.
- Notificaciones de Windows: la opción «Mostrar notificaciones…» (propiedad `NotificationSoundEnabled`) **gating total**: con ella desactivada no se muestra ningún globo (ni en alarma tocada ni en omitida). Con ella activa, `TrayIconService.ShowNotification` muestra globo con sonido (`ToolTipIcon.Info`).

### 2.4 Autostart y bandeja
- `AutostartService` escribe `"ruta\Alarmino.exe" /min` (o sin `/min` si «mostrar ventana al inicio») en `HKCU\...\Run`.
- Al cerrar la ventana → se oculta a la bandeja; «Salir» del menú de bandeja termina el proceso.

## 3. Persistencia y biblioteca de sonidos

- `%APPDATA%\Alarmino\`: `alarms.json`, `config.json`, `events.json`, `logs\alarmino-AAAA-MM-DD.log`, `sounds\`.
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

### 5.2 Casos (31 tests, xFact verde)
| Archivo | Cubre |
|---|---|
| `SchedulerEvaluatorTests` (8) | disparo al entrar en el minuto, no redispare dentro del mismo minuto, días de la semana, alarmas inactivas, **franjas omitidas por salto de tiempo** (equipo dormido), orden de coincidencias — con `FakeTimeProvider` |
| `ModelsAndStoreTests` (5) | flags de días, texto corto de días, **round-trip JSON** de alarmas/config/eventos, archivos ausentes → valores por defecto |
| `PresetSynthesizerTests` (9) | cada preset produce señal no vacía, duración esperada y **RMS audible** (> 0.05) |
| `FadeOutWaveStreamTests` (3) | la señal se mantiene antes del fade, llega **a ~silencio** al final y decae en la ventana de fade |
| `SoundLibraryTests` (3) | copia a biblioteca con nombre único, `Normalize` (migración), resolución de preset por archivo |

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
- Ciclo de instalación/desinstalación real (prueba manual; solo scripts de apoyo como `smoke-win.ps1`).
- Comportamiento con Windows suspendido (solo cubierto a nivel de scheduler con el reloj falso).