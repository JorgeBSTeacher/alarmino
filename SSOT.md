# Alarmino — SSOT (Single Source of Truth)

> Documento de referencia única para el desarrollo de **Alarmino**.
> Cualquier discrepancia entre este documento y el código debe resolverse actualizando uno de los dos (prevalece el código en ejecución, pero se registra aquí el cambio).
> Última actualización: 2026-09-11 · Versión v0.1.0

## 1. Visión general

**Alarmino** es una aplicación de escritorio para **Windows 10/11** que sirve para programar **sirenas/timbres de colegio**. El usuario añade alarmas con hora, días de la semana en que se repiten (modelo de la alarma de un teléfono móvil) y un archivo de sonido. Las alarmas pueden activarse/desactivarse, pero **siempre quedan guardadas**. La aplicación se **autoejecuta al iniciar Windows** para que las sirenas suenen incluso sin que nadie la abra.

## 2. Decisiones técnicas (punto 6 de requisitos)

| Decisión | Elección | Motivo |
|---|---|---|
| Motor / lenguaje | **C# .NET 8 + WPF (MVVM)** | Nativo Windows 10/11, ligero, audio MP3/WAV integrado vía **NAudio**, autostart simple (registro HKCU), UI nativa rápida de desarrollar |
| Reproducción audio | **NAudio** (`WaveOutEvent` + `Mp3FileReader`/`WaveFileReader`) | Soporte MP3 y WAV sin dependencias externas |
| MVVM | **CommunityToolkit.Mvvm** (source generators) | Código limpio, observable, patrón estándar WPF |
| Persistencia | **JSON** en `%APPDATA%\Alarmino\` (`alarms.json`, `config.json`, `events.json`) | Sencillo, editable y robusto para pocos datos |
| Cronómetro | `DispatcherTimer` a **500 ms** con **`ITimeProvider` inyectable** | Suficiente precisión para timbres; reloj falso en tests → determinista |
| Autostart | Clave `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` con arg `/min` | Arranque por usuario sin permisos de administrador |
| Vuelo/tray | **NotifyIcon** con menú contextual; cerrar = minimizar a bandeja | Sempre activa sin molestar |
| Instancia única | `Mutex` con nombre global `Global\Alarmino` | Evita procesos duplicados (bandeja única) |
| Instalador | **Instalador propio** (`tools\Alarmino.Installer`, self-extracting) — Inno Setup opcional en Windows | Instala en `%LocalAppData%\Alarmino`, crea accesos directos, autostart y clave de desinstalación; se compone **sin** requerir Windows: `scripts/package.sh` |
| Idiomas | Solo **español** | Requisito 2026-09-11 |
| Tema | Oscuro/claro conmutables | Requisito 2026-09-11 |

## 3. Requisitos funcionales

### 3.1 Gestión de alarmas
- **Crear / editar / eliminar** alarmas.
- Cada alarma tiene:
  - **Id** (`Guid`).
  - **Nombre (alias)** — obligatorio. Ej.: «Recreo», «Entrada 9:00», «Toque final».
  - **Hora** `HH:mm` (24h).
  - **Días** de repetición (bitmask L-V-D), estilo alarma de móvil. Mínimo 1 día.
  - **Sonido**:
    - Un **archivo personalizado** `.mp3` o `.wav` (rutas seleccionadas con diálogo `OpenFileDialog`), **o**
    - Un **sonido predeterminado** de la biblioteca incluida (sintetizados en código, sin copyright):
      - `Campana` — secuencia de tonos tipo campana.
      - `Timbre` — anillo escolar de doble tono.
      - `Tono escolar` — zumbido de tono prolongado.
  - **Modo de reproducción**:
    - `Completo`: se reproduce el archivo/preset de principio a fin.
    - `Duración personalizada`: se reproduce durante un número de **segundos** configurable.
  - **Repeticiones (N toques)**: el sonido suena, pausa, vuelve a sonar… `N` veces en el mismo evento (por defecto 1, pausa por defecto 1 s).
  - **Activa** (ON/OFF): conmutador por alarma. Las desactivadas se conservan.
- La **lista** se puede **ordenar por hora** o **por día** (desplegable persistente en `config.json`).
- **Botón Master ON/OFF** global: apaga/activa todas las alarmas de golpe, sin borrarlas.

### 3.2 Ejecución y sonido
- Cuando el cronómetro detecta que la hora coincide con una alarma **activa** programada para ese día:
  1. Reproduce el sonido según modo y repeticiones.
  2. Registra el evento «Tocada» en `events.json` y muestra una **notificación** toast (click → abre la app).
- Las alarmas solo suenan mientras el proceso está en ejecución; por eso la app **se autoejecuta** y **minimiza a la bandeja**.
- **Silenciar alarma activa**: opción en el menú de la bandeja que corta la reproducción actual e impide que la misma alarma vuelva a tocar hasta que pase de minuto.

### 3.3 Volumen
- **Volumen global** (0–100 %) en Configuración.
- **Botón Probar sonido** por alarma en el editor (previsualiza con el volumen global).

### 3.4 Configuración
- Volumen global.
- Tema **claro/oscuro**.
- **Autostart** activado/desactivado.
- Criterio de ordenación de la lista (hora / día).
- Idioma (solo español por ahora).

### 3.5 Registro de eventos
- Tabla con: **fecha/hora** del suceso, **nombre de la alarma**, **estado** (`Tocada` / `Omitida`).
- **Omitida**: alarma que debía sonar mientras el equipo estuvo dormido/apagado (el reloj solo detecta la última transición). No suena retroactivamente.
- Botón **Limpiar** historial.

### 3.6 Bandeja y cierre
- Cerrar ventana → minimiza a la **bandeja** (icono con menú: Abrir, Silenciar, Salir).
- **Salir** sí termina el proceso (el usuario debe ser explícito).
- Al arrancar con `/min` (autostart) se abre directamente en bandeja, sin ventana.

## 4. Requisitos no funcionales

| Área | Requisito |
|---|---|
| Plataforma | Windows 10/11 x64 |
| Runtime | .NET 8, publicación self-contained single-file (`win-x64`) |
| Rendimiento | Latencia de disparo del scheduler < 1 s tras la coincidencia de hora |
| Persistencia | Datos en JSON, atómicos (escritura a temp + rename) |
| Seguridad | Sin secretos; no sube datos a red; rutas locales |
| Calidad | Analizadores Roslyn activos (`EnforceCodeStyleInBuild`), `dotnet format` |
| Tests | xUnit + Coverlet (cobertura objetiva); CI GitHub Actions (ubuntu + windows) |
| Diagnóstico | Log a `%APPDATA%\Alarmino\logs\` para scheduler/audio |
| Empaquetado | Instalador self-extracting stub + `.exe` portátil publicado (`artifacts/AlarminoSetup.exe`) |

## 5. Arquitectura

```
src/Alarmino (WPF, net8.0-windows)
├── App.xaml(.cs)            App + arranque, single-instance, tray, temas
├── Models/                  Alarm, AppSettings, EventLogEntry, SoundPreset
├── Services/
│   ├── ITimeProvider.cs     reloj inyectable (determinismo en tests)
│   ├── JsonDataStore.cs     alarmas/config/eventos/log
│   ├── AudioService.cs      NAudio: Play/Preview; presets sintetizados
│   ├── SchedulerService.cs  timer 500 ms + evaluación + disparo
│   ├── AutostartService.cs  HKCU Run (+ /min)
│   └── TrayIconService.cs   NotifyIcon + menú
├── ViewModels/              MainViewModel, AlarmEditorViewModel, ...
├── Views/                   MainWindow, AlarmEditorDialog, SettingsView, EventsView
└── Resources/               iconos, estilos, plantillas, temas claro/oscuro

src/Alarmino.Tests (xUnit)   modelos, JsonDataStore, scheduler (reloj falso)
tools/Alarmino.Installer    stub instalador WPF self-extracting (zip adjunto al exe)
installer/AlarminoSetup.iss Inno Setup (opcional, solo Windows)
scripts/                     dev, test, debug, build, publish, package,
                             gen-sounds, analyse, smoke-win, bench (ver §7)
.github/workflows/build.yml  CI ubuntu (build+test+análisis) y windows (publish+instalador)
```

## 6. Riesgos y decisiones abiertas

| Riesgo | Mitigación / decisión |
|---|---|
| Equipo dormido o apagado durante una hora | Se registra la alarma como «Omitida», no suena retroactivamente |
| Archivo de audio movido/borrado | Al no encontrarse, se registra error en el log y notificación; alternativa: seleccionar otro |
| Volumen del sistema (muted) | La app usa volumen global 0–100 % sobre el dispositivo por defecto |
| Dos alarmas a la misma hora | Suenan en cola secuencial (según orden de lista), cada una registrada |
| Cambio de hora (DST) | Las horas se evalúan sobre reloj local; solo España/college gestionado por Windows |
| El exe self-contained pesa (~150 MB) | Aceptable; el instalador comprime el payload (zip) |
| Instalador gigante en Linux | Se compone vía Python **sin** Wine/Inno: stub + zip + marcas (formato `SelfExtractor`) |
| Permisos admin | No requeridos (instalación + autostart por usuario) |

## 7. Plan de scripts (testear, debuggear, mejorar)

Pendiente de enlazar desde `SSOT_SCRIPTS.md` o sección en README (ver commit de Fase 6):
`dev.ps1`, `test.ps1`, `debug.ps1`, `build.ps1`, `publish.ps1`, `package.ps1`/`.sh`,
`gen-sounds.ps1`, `analyse.ps1`, `smoke-win.ps1`, `bench.ps1`, `compose-installer.py`,
CI GitHub Actions.

## 8. Criterios de aceptación (v1)

1. Instalador funciona en Windows 10 y 11.
2. Añadir alarma con alias, hora, días, sonido (.mp3 y .wav y presets).
3. Modo completo y duración personalizada; repeticiones N toques.
4. Master ON/OFF y conmutadores individuales; las desactivadas persisten al reiniciar.
5. Autostart en `Run` con `/min` → abre en bandeja.
6. Orden por hora y por día.
7. Registro de eventos con Tocada/Omitida.
8. Tema oscuro/claro; volumen global; probar sonido.
9. `dotnet build` y `dotnet test` en CI pasan.