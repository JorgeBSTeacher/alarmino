# Feature: Reliable startup + user improvements

**Branch:** `alarmino-youtube` (existing)
**Created:** 2026-10-20
**Delivery strategy:** `ask-on-risk` (forecast ~770 authored lines > 400; chain strategy to be asked once before delivery/PR work)
**Runner:** `dotnet test` at repo root (global.json pins SDK)

## Objective

Fix all critical cold-boot failures in the YouTube-playlist alarm flow and implement the
user-approved improvements: session operation log, persistent MP3 fallback, day-window
anti-repeat, background download buffer, visible player status/diagnostics, and the alarm
days button-signaling bug.

## Problem / Why

On cold reboot the app sometimes plays nothing: silent alarm with no fallback (default
`OfflineFallbackFilePath` is null), a 30-minute prefetch dead zone (attempt marked before
the connectivity check), corrupt partial downloads trusted on next boot (`LoadExisting`
registers files without `TrackSanity`), and unpersisted `Consumed` state.

## Scope (authorized)

In scope: T1–T7 below.
Out of scope: propagating remote playlist deletions to disk (deferred by user decision;
buffer-only for T5), push/PR/merge (human-owned).

## Acceptance criteria

- Cold boot with no network at alarm time plays the fallback MP3 (never silence).
- A truncated file left by power loss is never served; it is discarded and logged.
- Offline prefetch attempt does NOT burn the 30-minute throttle; retry happens on the next
  sweep or a short retry window.
- Live downloads have an explicit timeout and fail into fallback, not hang.
- Consumed state survives restart (no accidental replay of same track).
- Session operation log exists under `%APPDATA%\Alarmino\logs` with timestamps + source.
- First run sets a bundled default sound as the offline fallback automatically.
- Tracks played in the last N days (default 7, configurable) are not picked again unless
  the whole playlist is exhausted (then repeat + log note).
- Background buffer keeps up to N (default 3) unconsumed tracks ready.
- Main window shows player state (Idle/Downloading/Playing) and elapsed seconds; a
  diagnostics view shows recent log lines.
- Editing an alarm's days refreshes the day-button visual state.

## Tasks

- [x] **T1 — Fix critical cold-boot failures** (core) — commit `dfa3bc4`
  - RDD review: unavailable (runtime not eligible for immutable receipt review;
    claude-code/codex only). Verification of record: writer `dotnet build` 0 err +
    `dotnet test` 73/73; parent spot check re-ran `dotnet test` 73/73 green.
  - Note: new `manifest.json` in cache dir persists consumed + expected bytes;
    `WithLiveTimeoutAsync` is a public static seam (no InternalsVisibleTo available).
  - `LoadExisting`/`TryTake`: validate recovered files with `TrackSanity.LooksPlayable`;
    discard + log failures (`YouTubeTrackCache.cs`, `TrackSanity.cs`).
  - `PrefetchCoreAsync`: connectivity check BEFORE `MarkPrefetchAttempt`; when offline do
    not mark the attempt (`YouTubeAudioResolver.cs:212-219`).
  - Explicit timeout (60 s) on live download path via linked `CancellationTokenSource`.
  - Persist `Consumed` in the manifest so it survives restart.
  - Route: delegated (writer trigger: 3+ files). Tests: RED first.
- [x] **T2 — Session operation log** — commit `1dc39df`
  - `OperationLog.cs` (`%APPDATA%\Alarmino\logs`), `RecentLines` cap 200, never-throws.
    Wired into resolver/cache/AudioService. Tests 5/5 (RED via CS0103 observed).
- [x] **T3 — Persistent offline fallback MP3** — commit `f14172b`
  - `OfflineFallbackSelector.cs` + `MainViewModel.EnsureOfflineFallback()` in `Initialize()`.
    Candidates come from runtime preset dir `%APPDATA%\Alarmino\sounds\presets` (the repo
    `defaultsound/` folder is gitignored source, NOT a runtime path). Tests 6/6.
  - Open follow-up: `AutostartService.cs` still uses `JsonDataStore.AppendLogLine`.
- [x] **T4 — Day-window anti-repeat history** — commit `efb8d29`
  - `PlayHistoryStore.cs` (`%APPDATA%\Alarmino\play-history.json`) + `AppSettings.NoRepeatDays`
    (default 7). Recorded in `AudioService.PlayOnceAsync` right after `output.Play()` succeeds
    — the only seam where the track is provably sounding. 11 tests; old settings JSON loads 7.
- [x] **T5 — Background download buffer** — commit `2038532`
  - `AppSettings.TargetBufferCount` (default 3) + `YouTubeTrackCache.GetBufferState`; filled
    inside the existing `PrefetchCoreAsync` path (no parallel loop), no deletion. 4+4 tests.
  - Wiring gap found by the writer and fixed by the parent: `MainViewModel:49` is the only
    `new YouTubeAudioResolver()` site, so both settings are now plumbed there and re-applied
    in `PersistSettings()`; otherwise T4/T5 were dead code.
- [x] **T6 — Visible player status + diagnostics view** — commits `164355b` + `1587f65`
  - Core `PlayerStatusFormatter` (7 tests, RED via CS0103); resolver `DownloadActivityChanged`
    event (Interlocked, exception/cancellation-safe); `PlayerStatusText` refreshed by the
    existing 500 ms `OnTick`; `Diagnóstico` tab in `SettingsWindow` reading `RecentLines`.
  - WPF/wiring parts are build-verified only (no WPF test host; `Alarmino.Tests` is net8.0
    and cannot reference the `net8.0-windows` project).
- [x] **T7 — Fix alarm days button signaling** — commit `2e5a50f`
  - Root cause: `DayMonday`…`DaySunday` setters wrote `_days[i]` but never called
    `OnPropertyChanged`, so the `CheckBox` binding never refreshed and a reopened editor showed
    stale day state. Fixed with a `SetDay` helper that notifies and skips no-op writes.
  - Build-verified only; see the test-hosting limitation above.

## Status: COMPLETE (all 7 tasks committed and verified)

Final verification: `dotnet build Alarmino.sln` → 0 warnings / 0 errors;
`dotnet test` → **111/111 passing** (baseline was 73; +38 new tests).

## Delivery budget

Running total ≈ **1,050 authored changed lines** across `dfa3bc4..1587f65` — above the ~400
advisory line budget, so the chosen `ask-on-risk` strategy requires asking for the chain
strategy before PR work. No push/PR/merge performed (human-owned). See "Next steps".

## Next steps (not done in this batch — human decisions)

1. Chain strategy for delivery (stacked-to-main vs feature-branch-chain) — required before PR work.
2. Open: `AutostartService.cs` still uses `JsonDataStore.AppendLogLine` instead of `OperationLog`.
3. Verify the WPF-only changes at runtime on Windows (status text, Diagnóstico tab, day buttons).

## Batch 2 (user-authorized after validation: prioritize stability/robustness)

- [x] **T8 — Playback speed must be 1.0x** — commits `a624b17` + `5515dae`
  - Root cause confirmed: `WaveOutEvent.Init(source)` passes the provider's declared
    `WaveFormat` straight to waveOut with NO resampling. YouTube m4a decodes at 48 kHz; a
    44.1 kHz Windows device plays it 48/44.1 = **8.84% fast**, matching exactly what the user
    heard. Same class of bug for any mp3/wav whose rate differs from the device.
  - Fix: new Core `PlaybackFormatSelector` (`NeedsResampling`/`ComputeTargetFormat`, 7 tests);
    device format read via WASAPI `AudioClient.MixFormat` (NAudio 2.2.1 has NO
    `WaveOut.GetCapabilities(...).SampleRate` — only `Channels`); `MediaFoundationResampler`
    (not `WaveFormatConversionStream`, which is PCM-only) wraps the FINAL source, so the
    truncate/fade wrappers keep operating in the source PCM16 domain. Dispose handled without
    double-dispose (`MediaFoundationTransform.Dispose` does not dispose its inner provider).
  - WPF/device wiring is build-verified only (MediaFoundation is Windows-only).
  - **Runtime confirmation on a real 44.1 kHz Windows box still pending — human-owned.**

- [x] **T9 — Full point 4: remote playlist sync with safe deletion** — commit `73c12f4`
  - `YouTubeTrackCache.RemoveTracksNotIn(playlistId, allowedVideoIds)` (returns removed paths,
    persists manifest, refuses an empty allowed-set as defense in depth);
    `IYouTubeAudioResolver.PrefetchAndSyncAsync` sharing the existing `_prefetchGate`;
    `MainViewModel.SyncYouTubePlaylists()` reusing the existing background trigger (no new timer).
  - Fail-closed: fetch throw/timeout/cancel ⇒ nothing touched. A file in `_pendingWrites` is
    never deleted.
  - Writer also found a real bug: the sweep was passing the playlist **URL** where the cache is
    keyed by playlist **id**, so nothing ever matched.
  - Tests: 6 new cache tests + 4 sync tests.

- [x] **T10 — Completeness by YouTube's declared count** (fix to the T9 guard, same commit `73c12f4`)
  - T9 shipped `if (remote.Count < MaxPlaylistItems) return;` — but 300 is the `CollectAsync`
    **read cap**, not a completeness signal. A normal playlist under 300 videos always looked
    "incomplete" ⇒ **deletion silently never happened**. Decision taken by the parent in favour
    of robustness over the arbitrary threshold.
  - Fix: new `PlaylistSnapshot(VideoIds, DeclaredCount)`; `Playlists.GetAsync` supplies YouTube's
    declared total (`Count ?? 0`, null for infinite/mix playlists). Deletion authorized only when
    ids non-empty AND `DeclaredCount > 0` AND `DeclaredCount <= MaxPlaylistItems` AND
    `ids.Count >= DeclaredCount`. Larger-than-cap playlists log and skip.

## Verification (batch 2)

- `dotnet build Alarmino.sln`: 0 warnings, 0 errors.
- `dotnet test`: **135/135 passing** (batch 1 baseline 111; +24 new), stable across 3 runs.
- Parent spot check: re-ran `dotnet test` before every commit.

## Status: batch 2 COMPLETE

## Batch 3 (user request: explicit startup verification)

User requirement (verbatim intent): if at boot there is not enough time to have a playlist
track ready, play the backup; but it IS necessary to verify that something is downloaded for
the NEXT alarm. So: never block startup on the network, always fall back to the MP3 when the
clock beats the download, and make the readiness of the download cache an explicitly checked
and logged startup condition.

- [ ] **T11 — Explicit startup readiness check for download state** → **DONE** (see Batch 3 below)
  - Bug to fix (parent-authored, in batch 2): `MainViewModel.EnsureOfflineFallback`
    (~:918-926) returns early when the configured fallback already exists and is valid, and
    only logs in the `chosen is null` case. On a normal boot the log never states that the
    fallback was validated — the exact boot path the user needs certainty about.
  - New: at startup, after resolving the playlists, explicitly check the download state per
    playlist via `YouTubeTrackCache.GetBufferState`/`IsFresh` and LOG the readiness verdict
    (ready count, whether a track was available, which playlist).
  - New: when a playlist that an alarm needs has NOTHING ready at boot, a download must be
    triggered immediately rather than waiting for the 30-minute sweep. The throttle must not
    swallow the boot-time case (this is the same class of bug as the offline prefetch dead
    zone): a cold boot with an empty cache must start downloading.
  - Preserve the user's rule: startup NEVER blocks on the network; if the alarm fires before a
    track is ready, the existing fallback path handles it (live download with 60 s timeout,
    then the MP3). Do not add a blocking wait.
  - Orphaned-cache consideration: when an alarm's playlist is changed, the previous playlist's
    cached track is never cleaned. Decide with the parent whether to address it here or defer;
    do NOT silently expand scope.
  - Route: delegated. Tests: RED first for the Core-testable parts (the readiness decision and
    the "boot must not be swallowed by the throttle" rule).

## Batch 3 COMPLETE (T11)

- [x] **T11 — Explicit startup readiness check for download state** — commits `5468e42` + `267b974`
  - `BootReadiness` (Core, pure): per-playlist verdict `TrackReadiness(PlaylistId, Ready, Target)`,
    distinguishes "N/M pistas listas" from "nada descargado; se descarga ahora".
  - `OfflineFallbackReadiness` (Core, pure): **fixes the silent-fallback bug** — the
    already-configured-and-valid boot path now logs "respaldo sin conexión validado", instead
    of returning with no log line at all (the normal boot path).
  - `CheckBootReadinessAsync` (disk-only, never touches the network, never throws) + explicit
    `IsPrefetchDue` on the interface; `PrefetchAsync(..., force: true)` exists as the
    boot-time escape from the 30-minute throttle.
  - `MainViewModel.StartBootYouTubeSequence()`: ONE background job — (1) boot readiness verdict,
    force-downloading any playlist with nothing ready (so an empty cache at boot no longer
    waits up to 30 min), then (2) the remote sync + safe deletion from T9/T10.
  - Startup still NEVER blocks on the network (user's rule): the 500 ms alarm timer starts
    first; if the alarm beats the download the existing fallback path handles it (live download
    with 60 s timeout → MP3 backup).
  - Writer also unified `PickRandomVideoIdAsync` onto the injectable `PlaylistSnapshot` seam,
    removing a duplicate remote read.
  - Tests: 150/150 (batch 2 baseline 135; +15 new), stable across 3 runs.

## Verification (batch 3)

`dotnet build Alarmino.sln`: 0 warnings, 0 errors. `dotnet test`: 150/150, stable x3.

## Open (deferred, not silently expanded)

- Human-owned: runtime validation on Windows; delivery/chain-strategy decision.

## Batch 4 — orphaned cache (user requested)

- [x] **T12 — Clean up playlists no longer used by any alarm** — commit `7b433cd`
  - Gap: changing an alarm from playlist A to B left A's entry in `_entries` and its file on
    disk forever. `ListStaleFiles` only deletes files with NO entry, `RemoveTracksNotIn` only
    acts on the id you pass, and the boot set only walks currently-referenced playlists. Orphans
    consumed the `MaxTracks = 3` budget and could evict a needed track.
  - `YouTubeTrackCache.RemovePlaylistsNotIn(allowedPlaylistIds)` — decides purely by playlist-id
    membership, persists the manifest, refuses an empty allowed-set **inside the cache** (an
    empty set is indistinguishable from "settings failed to load").
  - **Boundary enforced:** orphan sweep = local id membership, never a network call. "Video left
    the remote list" stays exclusively `RemoveTracksNotIn` (already fail-closed). Pinned by a test
    whose injected `playlistVideoIds` delegate throws if called.
  - Wired as step 3 of the existing boot job, AFTER sync, so a referenced playlist's failed
    fetch is never second-guessed by the sweep. 7 new tests.

- [x] **T13 — Sweep on save, not just at boot** — commit `88df23b`
  - T12 alone only ran at boot: changing a playlist with the app open left the orphan until the
    next reboot. Now `PersistAlarms()`/`PersistSettings()` request a sweep.
  - Naive `RemoveOrphanedPlaylists()` in the save path would run file I/O **on the UI thread**
    and be silently dropped by the `_prefetchRunning` latch exactly when a prefetch was running.
    Solution: coalescing `PendingWorkSignal` (Core + 6 tests) — a one-bit request that collapses
    bursty saves (30+ `PersistSettings` call sites) into one sweep, and whose pending bit survives
    a busy latch; `RunYouTubeBackground` releases the latch FIRST then drains, closing the
    lost-request window.
  - `MainViewModel` scheduling is build-verified only (`Alarmino.Tests` is net8.0 and cannot
    reference the net8.0-windows project); the coalescing primitive is the tested seam.
- [x] Batch 4 verification: `dotnet build` 0 warnings / 0 errors; `dotnet test` **163/163**
  (batch 3 baseline 150; +13), stable across 3 runs.




Remaining human-owned: runtime validation on Windows (WPF status/diagnostics, day buttons, and
the 1.0x playback speed on a real 44.1 kHz device), plus the delivery/chain-strategy decision.




## Verification (per task)

- `dotnet build Alarmino.sln` — must pass.
- `dotnet test` — all tests pass; new tests must show RED before implementation.
- Parent spot check: re-run one reported command before commit.

## Progress log

(to be filled per task: commit hash, tier assessment, verification evidence)
