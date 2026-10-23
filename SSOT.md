# Alarmino — SSOT (Single Source of Truth)

> Documento de referencia única para el desarrollo de **Alarmino**.
> Cualquier discrepancia entre este documento y el código debe resolverse actualizando uno de los dos (prevalece el código en ejecución, pero se registra aquí el cambio).
> Última actualización: 2026-10-23 · Versión v0.1.7 (descarga completa de playlist + reproducción aleatoria sin repetir 10 días)

## 1. Visión general

**Alarmino** es una aplicación de escritorio para **Windows 10/11** que sirve para programar **sirenas/timbres de colegio**. El usuario añade alarmas con hora, días de la semana en que se repiten (modelo de la alarma de un teléfono móvil) y un archivo de sonido. Las alarmas pueden activarse/desactivarse, pero **siempre quedan guardadas**. La aplicación se **autoejecuta al iniciar Windows** para que las sirenas suenen incluso sin que nadie la abra.

## 2. Decisiones técnicas (punto 6 de requisitos)

| Decisión | Elección | Motivo |
|---|---|---|
| Motor / lenguaje | **C# .NET 8 + WPF (MVVM)** | Nativo Windows 10/11, ligero, audio MP3/WAV integrado vía **NAudio**, autostart simple (registro HKCU), UI nativa rápida de desarrollar |
| Reproducción audio | **NAudio** (`WaveOutEvent` + `Mp3FileReader`/`WaveFileReader`) | Soporte MP3 y WAV sin dependencias externas |
| Velocidad de reproducción | **`PlaybackFormatSelector` + `MediaFoundationResampler`** (no `WaveFormatConversionStream`, que es solo PCM) | `WaveOutEvent.Init` entrega al dispositivo el `WaveFormat` declarado por el proveedor sin remuestrear: una pista de YouTube a 48 kHz sobre un dispositivo a 44.1 kHz suena ~8,8 % rápida. Se remuestrea a la **velocidad/frecuencia del dispositivo** (leída con WASAPI `AudioClient.MixFormat`) para garantizar **1.0x** |
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
    - Una **pista aleatoria de una playlist de YouTube** (URL de la playlist), **o**
    - Un **sonido predeterminado** de la biblioteca incluida (`presets\`):
      - `Bip avión` (`avion.mp3`) — aviso breve de cabina. **Por defecto del botón ALARMA.**
      - `Suave` (`suave.mp3`) — logotipo/acorde suave. **Por defecto del botón LLUVIA.**
      - `Incendio` (`alarmaincendio.mp3`) — alarma de incendio (~64 s). **Por defecto del botón ALARMA, que por eso suena una sola vez.**
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
- **Sonido de YouTube con playlist completa descargada (empieza puntual)**: la app descarga **la playlist completa** a la caché `%APPDATA%\Alarmino\cache\youtube` (hasta el tope de lectura `MaxPlaylistItems = 300`), de forma secuencial y en segundo plano. Cuando suena la alarma, se elige **al azar** una pista ya descargada, sin esperar red. Si hay varias, se evita repetir una pista reproducida en los **últimos 10 días** (`NoRepeatDays`, configurable, por defecto **10**); si **todas** las descargadas están dentro de esa ventana, se repite a propósito y queda registrado. Solo si la playlist **no tiene ninguna pista descargada** se descarga en el momento con **timeout de 60 s**, lo que puede retrasarla unos segundos. Las pistas se conservan hasta que la sincronización con la lista remota indique que ya no están en la playlist (se borran las ausentes); las playlists abandonadas se limpian por pertenencia local de id.
- **Velocidad 1.0x garantizada**: el audio se remuestrea al formato del dispositivo de salida antes de reproducirse, de modo que una pista de YouTube (habitualmente 48 kHz) **no suene acelerada** en un dispositivo a 44.1 kHz. Es la corrección del defecto de «la alarma suena más rápida».
- **Control de conectividad y respaldo**: si no hay pista descargada se comprueba la conexión (`generate_204` de YouTube, 4 s). Sin conexión, o si falla la descarga, se reproduce el **MP3 de respaldo** global; sin respaldo configurado la alarma queda en silencio.
- **Arranque nunca bloquea por red**: al encender, el temporizador de alarmas (500 ms) arranca **primero**; la parte de YouTube corre en segundo plano. Si una alarma gana la carrera, el camino de respaldo ya existente la atiende (descarga en vivo con timeout → MP3 de respaldo).
- **Verificación explícita del estado de descarga al arrancar**: un trabajo de fondo escribe en el log, por cada playlist en uso, si hay pistas descargadas o ninguna. Si una playlist **no tiene nada descargado, la descarga se fuerza en ese momento**, saltando la ventana de 30 minutos (una caché vacía al arrancar ya no espera al siguiente barrido). El veredicto se lee **solo del disco**, sin tocar la red, para no bloquear el arranque.
- **Sincronización con la lista remota y borrado seguro**: al arrancar se compara la caché con la playlist remota: se **descarga lo que falte** y se **borra de la caché lo que se haya quitado de la lista**. La completitud se comprueba contra el **número de pistas declarado por YouTube** (`Playlist.Count`), no contra el tope de lectura (`MaxPlaylistItems`, 300): ese tope es un límite de lectura, no una señal de lista completa, y usarlo impediría propagar borrados en playlists de menos de 300 vídeos. Si la consulta falla, llega vacía o truncada, **no se borra nada** (fail-closed); la negativa a borrar con un conjunto vacío vive dentro de la propia caché, como última línea de defensa.
- **Limpieza de playlists abandonadas**: si una alarma cambia de playlist, la anterior deja de usarla ninguna alarma. Sus pistas se retiran de la caché **por pertenencia local de id** (nunca consultando la red), tanto **al arrancar** como **al guardar ajustes o alarmas** (sin esperar al reinicio). No se confunde con «la descarga remota falló»: una playlist que sigue en uso conserva sus pistas pase lo que pase. Esta limpieza no ejecuta E/S en el hilo de la interfaz y agrupa las peticiones repetidas en una sola.
- **Antirrepetición de canciones**: una pista ya reproducida no vuelve a elegirse durante una **ventana de días** configurable (por defecto **10**). Si la ventana se agota sin candidatos entre las pistas descargadas, se registra en el log y se repite; nunca se deja la alarma muda.
- **Log de operaciones por sesión**: las decisiones de arranque, descarga, sincronización y borrado se registran, y las últimas líneas son visibles en Configuración → pestaña **Diagnóstico**.
- **Panel de diagnóstico en vivo (botón «Ver log»)** en la ventana principal: al pulsarlo se despliega un panel con, en primer plano, **dos indicadores con color** —**red** (con/sin conexión y cuándo se comprobó) y **pista descargada** (si hay alguna lista, y cuántas de cuántas)—, debajo el **detalle** (cada pista de la caché con su playlist, tamaño, si está consumida y a qué hora se descargó; estado del reproductor; si hay una descarga en curso) y debajo el **registro de la sesión en vivo**, que se va rellenando solo y se desplaza al final. Al abrirlo se carga el historial reciente; el estado se refresca cada 2 s y **la red se comprueba como mucho cada 15 s** (si el panel está cerrado no se toca la red). Los cambios de conexión quedan anotados en el propio log, de modo que el historial explica por qué una alarma usó el respaldo.
- **Estado visible del reproductor**: la ventana muestra qué está sonando y **los segundos transcurridos**, para saber de un vistazo que el audio avanza.
- **Archivos y manifiesto**: la caché persiste un `manifest.json` con la pista consumida y su tamaño, de modo que una descarga truncada se detecta en el siguiente arranque (peso mínimo + tamaño declarado) y se descarta.

### 3.3 Volumen
- **Volumen global** (0–100 %) en Configuración.
- **Botón Probar sonido** por alarma en el editor (previsualiza con el volumen global).

### 3.4 Configuración
- Volumen global.
- Tema **claro/oscuro**.
- **Autostart** activado/desactivado.
- Criterio de ordenación de la lista (hora / día).
- Idioma (solo español por ahora).
- **Botón ALARMA (rojo)** de la ventana principal: sonido (preset o archivo), **número de repeticiones** seguidas (por defecto **1**, porque el sonido de incendio dura más de un minuto) y **pausa entre toques** (por defecto 1 s). Las repeticiones se **concatenan** (audio completo → pausa → siguiente). Mientras suena, el botón **parpadea rojo/negro** (texto siempre visible).
- **Botón LLUVIA** de la ventana principal (acceso rápido al sonido de lluvia del patio): sonido configurable igual que ALARMA (preset o archivo), con **número de repeticiones** propio (por defecto 1). Ocupa la columna derecha de la cabecera junto a ALARMA. También **parpadea** mientras suena.
- **Botón PARAR ALARMA** (centro de la ventana, visible solo mientras hay reproducción): detiene ALARMA, LLUVIA o la alarma programada en curso, previa confirmación que muestra el **nombre** de lo que se va a parar («la alarma “Recreo”», «el botón LLUVIA», …).
- **Editar presets**: desde Configuración se puede **renombrar** cada sonido predeterminado y/o **asignarle un `.mp3`/`.wav`** que lo sustituya (el audio se copia a la biblioteca de sonidos). Los cambios se reflejan en todos los desplegables. «Restablecer» vuelve al audio original y al nombre de fábrica.
- **Configuración por pestañas**: General, ALARMA general, Botón de lluvia, Sonidos predeterminados.
- **Estilo ComboBox**: en tema oscuro el desplegable usa el mismo fondo y color de texto que el resto de la UI (se evita el default blanco del sistema).
- **Desinstalación robusta**: antes de borrar, se cierran los procesos de Alarmino en ejecución y un ayudante en segundo plano espera a que el desinstalador termine para eliminar la carpeta de instalación completa (con reintentos). Evita que la app «reaparezca» o queden restos.
- **Tres tipos de sonido** disponibles en cada alarma y en los botones ALARMA/LLUVIA: **sonido predeterminado**, **archivo local** (`.mp3`/`.wav`) y **pista aleatoria de una playlist de YouTube** (se pega la URL de la playlist). La app descarga **la playlist completa** a la caché local (hasta 300 pistas), elige al azar entre las **ya descargadas**, evita repetir en los **últimos 10 días** (por defecto) y repite solo si toda la lista descargada está dentro de la ventana. Si no hay ninguna descargada se descarga en el momento con timeout de 60 s; si no hay conexión o falla, suena automáticamente el **MP3 de respaldo** elegido en Configuración → General → «Respaldo sin conexión» (si no hay ninguno, la alarma queda en silencio).
- **Sincronización de los días del editor con los botones**: al editar los días de repetición de una alarma, el cambio se **notifica** para que los botones de la ventana principal reflejen el estado real. Antes, cambiar los días dejaba los botones desincronizados.

### 3.4 bis Sonidos predeterminados
- Los tres audios vienen **embebidos en el ensamblado** (no en archivos sueltos) para que funcionen también en el ejecutable único empaquetado. Al arrancar, `SoundLibrary.EnsureDefaultPresets()` los copia a `%APPDATA%\Alarmino\sounds\presets\{id}.mp3` **sin sobrescribir** lo que ya exista.
- Si alguno faltara (instalación dañada), `PresetSynthesizer` genera un beep equivalente como último recurso, para que ninguna alarma se quede muda.
- Derechos: los tres `.mp3` son obra de sus respectivos autores (obtenidos en Pixabay) y se usan con su permiso según las condiciones de esa plataforma. Los derechos de autor y demás derechos de esas obras pertenecen a sus titulares.

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
| Diagnóstico | Log a `%APPDATA%\Alarmino\logs\` para scheduler/audio; **log de operaciones por sesión** visible en Configuración → Diagnóstico |
| Empaquetado | Instalador self-extracting stub + `.exe` portátil publicado (`artifacts/AlarminoSetup.exe`) |
| Tests | **163 tests** en `Alarmino.Tests` (net8.0). `Alarmino.Tests` **no puede referenciar** el proyecto WPF (`net8.0-windows`): toda la lógica de UI/ViewModels es **verificada solo por compilación**, nunca por test unitario. Por eso la lógica verificable se extrae a `Alarmino.Core` como costura testeable |

## 5. Arquitectura

```
src/Alarmino (WPF, net8.0-windows)
├── App.xaml(.cs)            App + arranque, single-instance, tray, temas
├── Models/                  Alarm, AppSettings, EventLogEntry, SoundPreset
├── Services/
│   ├── ITimeProvider.cs     reloj inyectable (determinismo en tests)
│   ├── JsonDataStore.cs     alarmas/config/eventos/log
│   ├── AudioService.cs      NAudio: Play/Preview; presets (archivos) y respaldo
│   │                        + remuestreo al formato del dispositivo (1.0x)
│   ├── SchedulerService.cs  timer 500 ms + evaluación + disparo
│   ├── AutostartService.cs  HKCU Run (+ /min)
│   └── TrayIconService.cs   NotifyIcon + menú
├── ViewModels/              MainViewModel, AlarmEditorViewModel, ...
├── Views/                   MainWindow, AlarmEditorDialog, SettingsView
│                            (Configuración → Diagnóstico), EventsView
└── Resources/               iconos, estilos, plantillas, temas claro/oscuro

src/Alarmino.Core (net8.0, sin WPF — lógica verificable con tests)
└── Services/
    ├── YouTubeAudioResolver.cs      precarga, sincronización remota,
    │                                borrado seguro, veredicto de arranque
    ├── YouTubeTrackCache.cs         caché + manifest.json; RemoveTracksNotIn,
    │                                RemovePlaylistsNotIn (borrado fail-closed)
    ├── PlaybackFormatSelector.cs    NeedsResampling / ComputeTargetFormat (1.0x)
    ├── BootReadiness.cs             veredicto «N/M pistas listas» del arranque
    ├── OfflineFallbackReadiness.cs  veredicto del respaldo sin conexión
    ├── OfflineFallbackSelector.cs   elección del MP3 de respaldo
    ├── PlayHistoryStore.cs          ventana antirrepetición por playlist
    ├── OperationLog.cs              log por sesión (líneas recientes + evento en vivo)
    ├── LiveDiagnostics.cs           foto de estado (red, pistas, reproductor) y su texto
    ├── NetworkStatusMonitor.cs      vigilancia de la conexión sin martillear la red
    ├── TrackSanity.cs               integridad de la descarga (tamaño del manifiesto)
    └── PendingWorkSignal.cs         señal de un bit que agrupa barridos repetidos

src/Alarmino.Tests (xUnit)   modelos, JsonDataStore, scheduler (reloj falso),
                             caché de YouTube, arranque, formato de reproducción
tools/Alarmino.Installer     stub instalador WPF self-extracting (zip adjunto al exe)
installer/AlarminoSetup.iss  Inno Setup (opcional, solo Windows)
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
| YouTube cambia su API interna (la librería YoutubeExplode está en modo mantenimiento) | Si falla la descarga, el sonido cae automáticamente al MP3 de respaldo local; el error queda en el log |
| La descarga de una pista llega vacía, truncada o corrupta | Se valida antes de reproducirla (peso mínimo y comparación con el tamaño del manifiesto); si no es válida se descarta y se usa el MP3 de respaldo |
| El audio de YouTube suena acelerado (48 kHz sobre dispositivo a 44.1 kHz) | Se remuestrea al formato del dispositivo antes de reproducir; `MediaFoundationResampler` se aplica a la **fuente final** (no `WaveFormatConversionStream`, que solo admite PCM). Objetivo: **1.0x** |
| Una consulta a la playlist falla, llega vacía o truncada | **No se borra nada** de la caché (fail-closed). La negativa ante un conjunto vacío vive dentro de `YouTubeTrackCache`, no solo en quien llama. La completitud se mide contra `Playlist.Count` declarado por YouTube, nunca contra el tope de lectura de 300 |
| El arranque no da tiempo a comprobar la descarga antes de la primera alarma | El arranque **nunca** bloquea por red: el temporizador arranca primero y, si la alarma gana, se usa el respaldo o la descarga en vivo con timeout. La comprobación de estado corre en segundo plano y fuerza la descarga si la playlist está vacía |
| Caché ocupada por playlists que ya no usa ninguna alarma | Barrido por pertenencia local de id, al arrancar y al guardar. Un huérfano consumía una de las 3 plazas y podía desalojar una pista necesaria |
| El respaldo sin conexión existe pero el log no lo confirma | El arranque registra **siempre** el veredicto del respaldo (validado / establecido / ninguno); antes el camino normal —respaldo ya válido— no dejaba ninguna línea |

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
10. La pista de YouTube suena a **velocidad 1.0x** en un dispositivo real (remuestreo al formato de salida).
11. El arranque **no se bloquea** por red y, si la playlist está vacía, fuerza la descarga y lo registra en el log.
12. La caché se sincroniza con la lista remota (descarga lo que falte, borra lo que se quitó) sin borrar nunca ante una consulta fallida o vacía.

---

## 9. Mejoras propuestas (pendientes)

1. **Despertar el equipo para saltar las alarmas** con el PC suspendido/hibernado.
   Vía: timer del sistema (`SetWaitableTimer` con `fResume=TRUE`, P/Invoke sin admin) o
   Task Scheduler con `WakeToRun`. Opción en Configuración (activada por defecto) que arma
   el despertador para la próxima alarma. Limitaciones: no despierta un equipo apagado del
   todo; el plan de energía debe permitir temporizadores de reactivación (en ocasiones solo
   «importantes») y en algunas máquinas hay que habilitarlo en la BIOS.