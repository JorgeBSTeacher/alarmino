# Feature: Full playlist download + random play with a 10-day anti-repeat window

**Branch:** `alarmino-youtube` (existing)
**Created:** 2026-10-23
**Delivery strategy:** `ask-on-risk` (forecast ~1,150 authored lines > 400; ask once for the chain strategy before any PR work)
**Runner:** `/home/jorge/.dotnet/dotnet test src/Alarmino.Tests/Alarmino.Tests.csproj` (`dotnet` is not on PATH)

## Objective

When a sound (alarm, ALARMA/LLUVIA button, or settings sound) uses a YouTube playlist,
download the **complete playlist** into the local cache. At playback, pick a **random track
from the downloaded ones** and do not repeat a track played within the last **10 days**
(configurable, default 10).

## Problem / Why

The current cache is keyed by playlist and stores exactly one track per playlist
(`MaxTracks = 3`). `ResolveAsync` serves that one preloaded track, marks it consumed and
re-downloads a different one later. The user wants the whole playlist available locally and
a random pick from that local set, with a 10-day anti-repeat window. The existing discovery
also reported two defects that this feature must not inherit:

- Boot readiness reported the **global** unconsumed count for every playlist, so a playlist
  with nothing of its own looked ready when another playlist had a track.
- `PrefetchAndSyncCoreAsync` acquired `_prefetchGate` and then called `PrefetchCoreAsync`,
  which tried to acquire the same non-reentrant semaphore and bailed out: the boot sync
  never downloaded anything.

## Scope (authorized)

In scope:

- `YouTubeTrackCache` stores **many tracks per playlist** (playlist id + video id key), no
  global 3-track eviction, manifest keeps every track.
- Full-playlist background download in `YouTubeAudioResolver`: fetch the remote id list and
  download every missing track sequentially, resuming on later sweeps. Existing tracks are
  never re-downloaded or deleted by the fill path.
- `ResolveAsync` picks from **downloaded** tracks only, excluding tracks in the play-history
  window; when the whole downloaded set is inside the window it repeats and logs.
- `AppSettings.NoRepeatDays` default becomes **10**; the obsolete `TargetBufferCount`
  setting and buffer-target plumbing are removed.
- Disk-only boot readiness reports the **per-playlist downloaded** count and forces a
  download when that playlist has nothing.
- Diagnostics/readiness text adapted to "downloaded tracks" instead of "ready/target buffer".
- SSOT, version bump and tests updated with the behavior.

Out of scope: push/PR/merge (human-owned), remote deletion policy changes (kept as-is),
playlists larger than the existing `MaxPlaylistItems` read cap (300, kept and documented).

## Acceptance criteria

- A playlist with N ≤ 300 remote videos ends up with all N tracks in
  `%APPDATA%\Alarmino\cache\youtube` after the background fill, and a second pass downloads
  nothing.
- A track already on disk is never downloaded again just because it is older than 6 hours.
- `ResolveAsync` never needs the network when the playlist has at least one downloaded track
  inside the anti-repeat window.
- A track played within 10 days is not chosen again while any other downloaded track exists.
- If every downloaded track is inside the window, one is repeated deliberately and the log
  says so.
- Two playlists keep their tracks independently; readiness for one is not satisfied by the
  other.
- Boot sync actually downloads (the double-gate defect is fixed).
- A failed download of one track does not abort the fill of the rest.
- Corrupt/truncated files are still discarded on load.
- Settings from older versions (no `NoRepeatDays`) load with 10; the removed
  `TargetBufferCount` in old JSON is ignored.
- `dotnet build` 0 warnings/0 errors and all tests green.

## Tasks

- [ ] **T1 — Multi-track cache + full-playlist download + random-from-downloaded playback**
  (Core, app wiring and tests; one atomic work unit because the cache API change breaks the
  resolver and the readiness/diagnostics surfaces)
  - Key entries by `(playlistId, videoId)`; keep every track of every playlist, no
    `MaxTracks` eviction.
  - `Register` no longer replaces/deletes the previous track of the same playlist.
  - `TryTakeRandom(playlistId, excludeVideoIds, ...)` returns a random existing file and
    records it as consumed for diagnostics.
  - `GetVideoIds`/`GetDownloadedCount`/`TotalDownloadedCount`.
  - `RemoveTracksNotIn` removes every stale track of that playlist; `RemovePlaylistsNotIn`
    removes every track of abandoned playlists. Both keep the empty-set refusal and the
    pending-write protection.
  - `LoadExisting` and the manifest recover **all** tracks per playlist (migration from the
    old one-per-playlist manifest must work).
  - Remove `IsFresh`, `PeekVideoId`, `Prune`, `MaxTracks` and `GetBufferState`.
  - Injectable download seam so the fill is testable without the network.
  - `PrefetchCoreAsync`: fetch remote ids once per attempt, download every missing id
    sequentially, continue after a single failure, log progress and completion.
  - Extract the gate-free core and call it from `PrefetchAndSyncCoreAsync` so the boot sync
    downloads (fix the double-gate defect).
  - Remove `MaxTrackAge`/freshness skipping; a downloaded track stays.
  - `ResolveAsync`: random downloaded track excluding the 10-day history (and
    `LastVideoId`); repeat only when the whole downloaded set is inside the window; live
    download + fallback path only when the playlist has nothing downloaded.
  - Boot readiness per playlist from disk (`Downloaded > 0`), not a global count.
  - `NoRepeatDays` default 10.
  - `AppSettings.NoRepeatDays = 10`; remove `TargetBufferCount`.
  - `TrackReadiness` and `DiagnosticsSnapshot`/`DiagnosticsFormatter` report downloaded
    tracks; `MainViewModel` stops plumbing the removed buffer target and updates the
    status text.
  - Route: delegated writer. Tests: RED first.
- [ ] **T2 — SSOT, version and feature close**
  - Update SSOT sections (download policy, anti-repeat 10 days, cache shape, diagnostics,
    risks, acceptance) and version to 0.1.7 (`SSOT.md`, `Alarmino.csproj`,
    `installer/AlarminoSetup.iss`).
  - Final full verification and feature-document close.
  - Route: inline (mechanical docs after code is verified).

## Checks

- `/home/jorge/.dotnet/dotnet build Alarmino.sln` → 0 warnings / 0 errors.
- `/home/jorge/.dotnet/dotnet test src/Alarmino.Tests/Alarmino.Tests.csproj` → all green.
- New tests must fail (RED) before the implementation that makes them pass.

## Progress log

(to be filled per task: commit hash, verification evidence, review assessment)
