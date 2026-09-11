# Alarmino 🔔

Aplicación de escritorio para **Windows 10/11** que programa **sirenas/timbres de colegio**: añade alarmas con hora y días de repetición, elige un sonido (`.mp3`, `.wav` o predeterminado) y déjala sonar sola. Se autoejecuta al iniciar Windows y permanece en la bandeja.

> Especificación de referencia: [SSOT.md](SSOT.md)

## Características

- ⏰ Alarmas con **alias**, hora y días de la semana (modelo alarma de móvil).
- 🔊 Sonidos: **`.mp3` / `.wav`** personales o **presets integrados** (Campana, Timbre, Tono escolar — sintetizados, sin copyright).
- ▶️ Modos: reproducción **completa** o **duración personalizada**.
- 🔁 **Repetir N toques** con pausa configurable.
- 🔘 **ON/OFF** individual por alarma + **botón general** (master) que apaga todas.
- 📅 **Ordenar** la lista por hora o por día.
- 🖱️ Botón **Probar sonido**, **volumen global**, **tema claro/oscuro**.
- 📋 **Registro de eventos** (Tocada / Omitida).
- 🗔 Minimiza a la **bandeja**; cierre real solo desde el menú "Salir".
- 🚀 **Autoejecución** al iniciar sesión (bandeja directamente, con `/min`).
- ⚡ Instancia única y persistencia en `%APPDATA%\Alarmino`.

## Requisitos

- Windows 10/11 (x64).
- Para compilar: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows o Linux).
- Para el instalador: [Inno Setup 6](https://jrsoftware.org/isinfo.php).

## Build y ejecución

```powershell
# Windows — compilar y ejecutar
powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 -R

# Linux/CI
./scripts/test.sh
./scripts/build.sh
./scripts/publish.sh      # -> artifacts/win-x64/Alarmino.exe (portátil)
```

## Generar el instalador

```powershell
scripts\package.ps1       # requiere Inno Setup + publish previo
# -> artifacts\AlarminoSetup.exe
```

O mediante CI: el workflow `build.yml` sube el `.exe` y el instalador como artefactos.

## Scripts del proyecto

| Script | Qué hace |
|---|---|
| `scripts/dev.ps1` | Compila Debug y ejecuta (Windows) |
| `scripts/debug.ps1` | Compila Debug con símbolos (ideal con breakpoints) |
| `scripts/test.ps1` / `test.sh` | Tests + cobertura (Coverlet/reportgenerator) |
| `scripts/build.ps1` / `build.sh` | Build Release `win-x64` |
| `scripts/publish.ps1` / `publish.sh` | Publica `.exe` self-contained single-file |
| `scripts/package.ps1` | Compila el instalador Inno Setup |
| `scripts/analyse.ps1` / `analyse.sh` | `dotnet format` (estilo + analizadores) |
| `scripts/bench.ps1` / `bench.sh` | Mide rendimiento y latencia del scheduler |
| `scripts/smoke-win.ps1` | Prueba de humo en Windows (bandeja, autostart, instancia única) |
| `scripts/gen-icon.ps1` | Regenera `Resources/alarmino.ico` |
| `scripts/install-tools.ps1` | Instala herramientas de soporte |

## Estructura

```
src/Alarmino          # App WPF (MVVM, CommunityToolkit.Mvvm, NAudio)
src/Alarmino.Core     # Lógica pura (modelos, scheduler, persistencia) — testeable en Linux
src/Alarmino.Tests    # xUnit + Coverlet
tools/Alarmino.Bench  # Bench del scheduler (throughput + latencia)
installer/            # Script Inno Setup
scripts/              # Plan de scripts (dev/test/debug/build/publish/…)
.github/workflows     # CI Ubuntu + Windows
```

## Estado

Versión 0.1.0 — primera versión funcional. Ver criterios de aceptación en el [SSOT](SSOT.md), sección 8.