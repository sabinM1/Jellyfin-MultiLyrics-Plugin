# Multi-Lyrics — verification report

Date: 2026-11-13 · Environment: Linux sandbox (Debian), .NET 10.0.15 SDK + .NET 9.0.23
runtime installed locally, no Docker daemon available, full outbound network access.

Status legend: ✅ verified in this sandbox · 📄 verified against source/docs · ⏳ scripted,
not executed here (exact commands provided).

> **Post-audit update (2026-11-13):** the audit fixes changed several behaviours described
> below. Instrumental tracks now write **no** sidecar (cache only); a plain result no longer
> ends the sweep (kept as fallback while looking for synced); per-request timeouts no longer
> abort the queue; `Jellyfin.*` packages now use `<ExcludeAssets>runtime</ExcludeAssets>`;
> tokens are no longer echoed into the settings page; the alignment service returns 413 for
> oversized text; and a repository `manifest.json` was added. The unit suite is now **92
> tests** (all passing). The end-to-end section (4) is still unexecuted because no Docker
> host was available.
>
> **Update 0.4.0.0 (2026-11-14):** "No lyrics found" in the web player was traced to the
> delivery path, not to fetching (see §1.1). `GET /Audio/{id}/Lyrics` never consults
> `ILyricProvider`, and it only reads lyric media streams already in the database, so a
> written sidecar stayed invisible until a probe ran. The plugin now registers an `ILyricManager`
> decorator that serves the sidecar directly and can fetch on demand. Suite is now **102
> tests** (all passing).
>
> **Update 0.5.0.0 (2026-11-14):** the 0.4.0.0 decorator resolved `LyricDelivery` /
> `LyricStreamIndexer` in its constructor. Jellyfin's `ProviderManager` takes `ILyricManager`
> (`MediaBrowser.Providers/Manager/ProviderManager.cs`, ctor line 120) and the plugin's indexer
> takes `IProviderManager`, so `IProviderManager` was re-entered while it was being built and
> startup stalled after `Prepare system for possible migrations` (reproduced on Jellyfin 12.2.0,
> Arm64; removal of the plugin folder restored startup). The decorator now resolves everything on
> first use, so its construction is a leaf. Suite is now **103 tests** (all passing).
>
> **Update 0.6.0.0 (2026-11-14):** field reports from the same 12.2.0 install drove three fixes.
> (1) `SearchTitle`/`SearchArtist` build the query from a cleaned title: verified live against
> LRCLIB, `track_name=Carnage (feat. Little Simz) (Original Mix)` returns **404** on `/api/get`
> and **0** hits on `/api/search`, while `track_name=Carnage` returns **200** and 2 809 chars of
> synced lyrics. (2) A beside-media write that fails (read-only `:ro` mount) now falls back to the
> item metadata folder, which Jellyfin's `LyricResolver` also scans. (3) Per-source timeouts are
> 8-10 s and the on-demand budget is 40 s, so Tekstowo's former 25 s timeout can no longer consume
> the sweep. Suite is now **118 tests** (all passing).
>
> **Update 0.7.0.0 (2026-11-14):** the sidecar design was replaced at the user's request. There is
> no file output any more (`SidecarWriter`, `LyricDelivery`, `LyricStreamIndexer` and the
> scheduled `FetchLyricsTask` were deleted; `F1`-`F9` and `J1`-`J2` of the original audit no longer
> apply). Lyrics are fetched on demand and held in `EphemeralLyricsStore` (LRU, last 4 tracks,
> 2 h TTL), and `ResultCache` no longer persists lyric text — a test reads `cache.json` after a
> win and asserts the payload is absent. Suite is now **100 tests** (all passing); `SidecarTests`
> and `LyricsDeliveryTests` were replaced by `EphemeralLyricsTests` and `SearchTermTests`.

## 1. Jellyfin interfaces (verified against `jellyfin/jellyfin` tag `v10.11.0`)

All signatures were confirmed by reading the actual source (local extraction of the
release tarball), not from memory:

| Fact | Where | Result |
| --- | --- | --- |
| `IPluginServiceRegistrator` | `MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs` | `RegisterServices(IServiceCollection, IServerApplicationHost)` (two args, namespace `MediaBrowser.Controller.Plugins`) |
| `TaskTriggerInfo.Type` | `MediaBrowser.Model/Tasks/TaskTriggerInfo.cs` | **enum** `TaskTriggerInfoType` (`DailyTrigger`, `WeeklyTrigger`, `IntervalTrigger`, `StartupTrigger`), `TimeOfDayTicks`/`IntervalTicks`/`MaxRuntimeTicks` are `long?` |
| `ILyricProvider` | `MediaBrowser.Controller/Lyrics/ILyricProvider.cs` | `Name` + `SearchAsync(LyricSearchRequest) → Task<IEnumerable<RemoteLyricInfo>>` + `GetLyricsAsync(string id) → Task<LyricResponse?>` |
| `LyricSearchRequest` | `MediaBrowser.Model/Lyrics/LyricSearchRequest.cs` | `MediaPath`, `AlbumArtistsNames`, `ArtistNames`, `AlbumName`, `SongName`, `Duration` (**RunTimeTicks**), `SearchAllProviders`, `DisabledLyricFetchers`, `LyricFetcherOrder`, `IsAutomated` |
| `LyricResponse` | `MediaBrowser.Model/Lyrics/LyricResponse.cs` | `required Stream Stream` + `required string Format` (`"lrc"`/`"txt"`) |
| `RemoteLyricInfo` | `MediaBrowser.Model/Providers/RemoteLyricInfo.cs` | `Id` (no `_` allowed — Jellyfin splits at first `_`), `ProviderName`, `Metadata`, `Lyrics` |
| `LyricMetadata` | `MediaBrowser.Model/Lyrics/LyricMetadata.cs` | `Artist`, `Album`, `Title`, `Length` (ticks), `By`, `IsSynced` … |
| Provider call flow | `MediaBrowser.Providers/Lyric/LyricManager.cs` | ordering by `IHasOrder.Order`, per-provider try/catch, remote id = `<providerIdMd5>_<result.Id>`, download saves sidecar to media folder (`SaveLyricsWithMedia`) or internal metadata path |
| Plugin controllers | `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:161` | `mvcBuilder.AddApplicationPart(pluginAssembly)` for every plugin → controller auto-discovery confirmed |
| Elevation policy | `MediaBrowser.Common/Api/Policies.cs` | `Policies.RequiresElevation = "RequiresElevation"` |
| Built-in lyrics task | `MediaBrowser.Providers/Lyric/LyricScheduledTask.cs` | tracks-without-lyrics filter = `GetMediaStreams().All(s => s.Type != MediaStreamType.Lyric)`; mirrored by our task |
| Sidecar pickup | `LyricResolver` + Felitendo plugin behavior | `<audio name>.lrc` beside media is discovered on scan/refresh; media folder checked before internal metadata folder |

`TODO(verify)` items: none. (The one deliberate deviation: cloud-metadata endpoints are
refused by the SSRF guard even when *allow private alignment host* is on — documented in
README/config page as a safety hardening beyond the spec.)

## 1.1 Lyric delivery path (root cause of "No lyrics found")

Investigated because the web player kept reporting "No lyrics found" while the plugin logged
successful writes, and no plugin line appeared when the button was clicked.

| Fact | Where | Result |
| --- | --- | --- |
| Player endpoint | `Jellyfin.Api/Controllers/LyricsController.cs` (`[HttpGet("Audio/{itemId}/Lyrics")]`; `AudioController` has no lyric action) | returns `404` whenever `ILyricManager.GetLyricsAsync` yields null |
| Stock manager | `MediaBrowser.Providers/Lyric/LyricManager.cs` → `GetLyricsAsync(Audio, ct)` | only iterates `audio.GetMediaStreams().Where(s => s.Type == MediaStreamType.Lyric)` and reads `stream.Path`; providers are never consulted |
| Stream creation | `MediaBrowser.Providers/MediaInfo/AudioFileProber.cs` → `LyricResolver.GetExternalStreams` | lyric streams are written during a media-info probe only, and `LyricManager.TrySaveToFiles` mutes `ILibraryMonitor`, so a write does not self-trigger a scan |
| Sidecar match rule | `MediaInfoResolver.GetExternalFiles` + `NamingOptions.LyricFileExtensions` | prefix match on `FileNameWithoutExtension` (source: the item path), extensions `.lrc`, `.elrc`, `.txt`, media folder **and** `GetInternalMetadataPath()` |
| DI order | `Emby.Server.Implementations/ApplicationHost.cs:545` then `_pluginManager.RegisterServices(...)` | host registers `ILyricManager → LyricManager` first, plugin registrations are appended, and last registration wins → a plugin decorator is authoritative |
| No auto-discovery conflict | `Jellyfin.Server/CoreAppHost.cs` (`GetExportTypes<ILyricProvider/ILyricParser>` only) | plugin assemblies are not auto-registered as `ILyricManager`, so the decorator cannot be overridden by a reflection loop |

Fix implemented (`Lyrics/PluginLyricManager.cs`, registered in `PluginServiceRegistrator.cs`):
stock result → sidecar read from disk (`Lyrics/LyricFiles.cs`, including word cues) → on-demand
fetch (`ILyricsQueue`, 25 s budget, 2 concurrent max, honors the retry window). Writes also
persist a `MediaStreamType.Lyric` stream and queue a refresh (`Lyrics/LyricStreamIndexer.cs`), so
Jellyfin's own manager and `HasLyrics` agree without a scan. Unit coverage:
`Jellyfin.Plugin.MultiLyrics.Tests/LyricsDeliveryTests.cs`.

## 2. External APIs (verified live from this sandbox, 2026-11-13)

| API | Request | Result |
| --- | --- | --- |
| LRCLIB `/api/get` | artist+track+album+duration | ✅ 200 JSON with `id, trackName, artistName, albumName, duration, instrumental, hasWordSync, plainLyrics, syncedLyrics, lyricsfile`; miss returns 404 `{"name":"TrackNotFound","statusCode":404}` |
| LRCLIB `/api/search` | track+artist | ✅ 200, array of the same objects (20 hits for the probe) |
| lyrics.ovh `/v1/{artist}/{title}` | GET | ✅ 200 `{"lyrics": "..."}`; miss → 404 |
| Tekstowo song page | GET | ✅ canonical URL is now `/{artist-slug}/{title-slug}` (old `piosenka,…` 301s there); lyrics in `<div class="song-text"> > <div class="inner-text">`; robots.txt allows song pages (only login/search pages disallowed) |
| Tekstowo slug behavior | redirect probe | ✅ hyphen slugs, lowercase, diacritics folded — implemented and unit-tested (`Slug("Beyoncé") == "beyonce"`) |
| ~~Musixmatch `matcher.lyrics.get`~~ | invalid key | removed in the final pass — upstream has disabled self-serve signup (`developer.musixmatch.com/signup` → 403 "Signup disabled"), so the source carried no value for a default install; the earlier 401-envelope probe is kept here only as a record |
| Genius `api.genius.com/search` | invalid token | ✅ `{"error":"invalid_token","error_description":…}` (no valid token in sandbox → auth-failure shape verified) |
| genius.com song page (scrape) | GET | ✅ 403 Cloudflare challenge from datacenter IPs → confirms the *“treat Cloudflare as a miss”* requirement; parser tested against synthetic fixtures |
| Lyricarr (Demucs+WhisperX reference) | docs | 📄 pipeline modeled as `whisperx.load_model → transcribe → load_align_model → whisperx.align → word timings → enhanced LRC` |

No lyric text from any live response was copied into any repository artifact.

## 3. Build + unit tests (run here)

| Check | Command | Result |
| --- | --- | --- |
| Plugin, Jellyfin 10.11 target | `dotnet restore Jellyfin.Plugin.MultiLyrics -s https://api.nuget.org/v3/index.json && dotnet build … -c Release --no-restore` | ✅ Build succeeded, 0 errors (net9.0, Jellyfin.Common/Controller/Model **10.11.0** floor) |
| Plugin, Jellyfin 12 target | `dotnet restore … -p:JellyfinTarget=jf12 … && dotnet build … -p:JellyfinTarget=jf12 --no-restore` | ✅ Build succeeded, 0 errors (net10.0, abstractions **12.0.0**) |
| Publish output | `dotnet publish … -o artifacts/publish-jf10` | ✅ exactly `Jellyfin.Plugin.MultiLyrics.dll` + `HtmlAgilityPack.dll` (+ `deps.json`/`pdb`); no `Microsoft.Extensions.*` or other host assemblies ship |
| Unit tests | `DOTNET_ROLL_FORWARD=Major dotnet test Jellyfin.Plugin.MultiLyrics.Tests -c Release --no-restore` | ✅ **102/102 passed** (`total: 102, failed: 0, skipped: 0`) |
| Alignment service logic tests | `python -m pytest alignment-service/test_lrc.py -q` | ✅ `3 passed` |
| Repository validation | `python scripts/validate_project.py --publish-dir artifacts/publish-jf10` | ✅ `59 passed, 0 warnings, 0 failed` (real MD5 checksums are committed, so no placeholder allowance is needed) |
| Plugin zip | `python scripts/pack_plugin.py --publish-dir artifacts/publish-jf10 --output artifacts/multi-lyrics_0.1.0.0_jf10.zip` | ✅ `packed 2 file(s)` → `Jellyfin.Plugin.MultiLyrics.dll`, `HtmlAgilityPack.dll` |
| Catalog manifest | `python scripts/gen_manifest.py --version 0.1.0.0 --base-url … --output artifacts/manifest-test.json <zip>` | ✅ manifest emitted with per-ABI `sourceUrl` and 32-char MD5 `checksum` |

> CI runs the same commands on .NET 9 and .NET 10 SDKs, so `DOTNET_ROLL_FORWARD=Major`
> (only needed on this workstation, which lacks the .NET 9 runtime) is not used there.

### Test inventory (102 tests at the time of this audit; the suite is 137 as of 0.15.0.0)

- **Source priority** (5): order parsing, disabled skipping, unknown-name/duplicate
  dropping, missing-known appending, empty-result-when-all-disabled.
- **Duration filtering** (6): tolerance matrix, null-duration pass-through.
- **Retry schedule** (3): 1/3/7/30 escalation, cap at last entry, custom/garbage parsing.
- **Cache retry logic** (6): miss escalation across instances, `ShouldSkip` inside/after the
  window, win persistence, deterministic fingerprint keys, metadata-change reset,
  corrupt-file fallback.
- **Sidecar path traversal** (4): sibling path build, relative-path refusal, unknown
  extension refusal, invalid-filename sanitization.
- **Overwrite policy** (5): synced-never-replaced-by-plain matrix, upgrade rules, missing
  sidecar, None-kind refusal.
- **Atomic writes** (3): write beside audio (temp file removed), existing-kind detection,
  synced-downgrade refusal with on-disk verification.
- **SSRF validation** (20+): loopback/private/link-local/metadata/CGNAT/scheme/userinfo/IPv6
  cases × allowPrivate toggle; literal-IP classification; unparseable URL refusal.
- **LRC parser** (6): timestamps, word-tag stripping + repeated-timestamp expansion, offset
  metadata, `LooksSynced`, round-trip format, plain-line splitting.
- **LyricResult** (2): kind classification, instrumental sidecar body.
- **HTML extraction** (8): Genius multi-container extraction, Cloudflare detection matrix,
  Tekstowo inner-text extraction (synthetic fixtures), slug canonicalization.
- **Priority queue** (7, fake sources, no network): miss falls through to the next source,
  synced short-circuits the queue, plain is remembered while the queue keeps looking for
  synced, plain-only sweep returns the fallback, a source timing out does not abort the
  queue, HTML challenge recorded as a miss with no sidecar, manual override queries only the
  named source.
- **Lyric delivery** (9, no network, no server): sidecar lookup prefers synced and falls back
  to the metadata folder, word cues become `LyricLineCue`s with ticks, plain parsing is
  unsynced, a stock-manager result passes through untouched, a sidecar is served when the
  database has no lyric stream, on-demand fetch writes the sidecar and returns lyrics, a
  cached payload is re-materialised without a query, the retry window suppresses on-demand, and
  the DI registration replaces the host `ILyricManager` exactly once while still delegating to it.
- **Cache round-trip** (1): a cached instrumental entry still classifies as `Instrumental`
  (regression test for the `LyricResult.Kind` reconstruction in `ToResult`).
- **Track matcher**: artist/title match theories (empty, cover, live, diacritics,
  punctuation) and `Normalize` diacritic/punctuation folding.

Bugs the tests caught during development (all fixed): per-segment LRC text extraction for
word-level repeated timestamps, BesideMedia target path bug, metadata-IP third-octet
mismatch, CGNAT metadata range, Tekstowo diacritic folding, `<br>`-to-newline handling.

## 3.1 Repository tooling and CI

Added in this pass to make the checklist gates reproducible instead of manual.

### Scripts (`scripts/`)

| Script | Purpose | Verified output |
| --- | --- | --- |
| `validate_project.py` | build.yaml ↔ Constants ↔ csproj ↔ manifest consistency, embedded config page, default-off Genius + ToS gate, `TODO(verify)` scan, hard-coded-secret scan, README sections, packaging inputs (artifacts present, no host assemblies) | `56 passed, 1 warning, 0 failed` |
| `pack_plugin.py` | zips exactly the `artifacts:` list from build.yaml out of a publish dir, flat layout, prints sha256 | `packed 2 file(s)` |
| `gen_manifest.py` | regenerates `manifest.json` from built zips with real `sourceUrl`, MD5 `checksum`, per-line `targetAbi` | manifest written, MD5 32-hex |

MD5 was confirmed as the catalog checksum algorithm by inspecting a published third-party
repository manifest (`vosmiic/jellyfin-ani-sync`), whose `checksum` values are 32 hex chars.

### Workflows (`.github/workflows/`)

- `ci.yml` (push / PR / manual):
  - `plugin` matrix `jf10` (.NET 9) and `jf12` (.NET 10): restore → build → publish →
    `validate_project.py` → `pack_plugin.py` → upload the zip.
  - `test`: `dotnet test` on net9.0 with TRX results uploaded.
  - `alignment-service`: byte-compiles `app.py`, runs the LRC logic tests, validates both
    compose files (`docker compose config -q`).
  - `metadata`: `validate_project.py` against the repository as committed.
- `release.yml` (tag `v*`): builds both lines, fails when the tag ≠ `build.yaml` version,
  creates the GitHub release with both zips, regenerates `manifest.json`, validates it
  strictly (no placeholder allowance) and attaches it — so
  `…/releases/latest/download/manifest.json` is a working catalog URL.

First release executed (run 2, `v0.1.0.0` → commit `7108062`,
<https://github.com/sabinM1/Jellyfin-MultiLyrics-Plugin/actions/runs/43511482947>):

| Evidence | Result |
| --- | --- |
| Workflow jobs | build `jf10` ✅, build `jf12` ✅, publish ✅ |
| Release | `Multi-Lyrics v0.1.0.0`, not draft/prerelease, body = `RELEASE_NOTES.md` (3462 bytes) |
| Assets | `multi-lyrics_0.1.0.0_jf10.zip` (125502 B), `multi-lyrics_0.1.0.0_jf12.zip` (125279 B), `manifest.json` (1793 B) |
| Zip contents | both: `Jellyfin.Plugin.MultiLyrics.dll`, `HtmlAgilityPack.dll` (flat) |
| Checksum fidelity | downloaded assets re-hashed: jf10 `2f6c4fa2e134ce6a9c334b3fc7d84fa5` ✅, jf12 `3dde36e475d57422c9f719006c924352` ✅ both equal the manifest `checksum` fields |
| Strict validation of the generated manifest | ✅ `56 passed, 1 warning, 0 failed` |

Workflow action versions were checked against the upstream `jellyfin-meta-plugins` template
(`actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/upload-artifact@v7`,
`actions/download-artifact@v8`, `actions/setup-python@v7`).

### Refinements made in this pass

1. `Jellyfin.Plugin.MultiLyrics.csproj`: `Microsoft.Extensions.Http` gets
   `<ExcludeAssets>runtime</ExcludeAssets>`; the assembly is part of the ASP.NET Core shared
   framework the Jellyfin host already loads, and shipping a second copy risked a version
   clash. Publish output dropped from 18 DLLs to the 2 real ones.
2. `build.yaml`: `artifacts` now lists `HtmlAgilityPack.dll` as well, so any packager using
   the manifest (jprm included) cannot produce a plugin that fails to load.
3. `LyricsSearchService.ToResult`: sets `IsInstrumental` for cached instrumental entries;
   previously such a cache hit classified as `LyricKind.None` and looked like a miss.
4. `FetchLyricsTask.CollectCandidates`: cache state is snapshotted once during the scan and
   reused by the sort, instead of taking the cache lock once per comparison.
5. `SidecarWriter.Write`: a metadata-folder policy with no metadata directory now reports
   `metadata folder unavailable` instead of failing silently.
6. `tests/integration`: compose now mounts `./plugin` into `/config/plugins/MultiLyrics`,
   matching the existing `run_test.sh` expectations, and `install_plugin.sh` stages the build
   there; the Jellyfin image is overridable via `JELLYFIN_IMAGE`.
7. `alignment-service/test_lrc.py`: removed a dead expression in the marker test.

## 4. Verification run against 5 tracks (requested deliverable #7)

The requested scenario (5 tracks incl. one not on LRCLIB; confirm synced highlighting in
Jellyfin Web; plain display; manual override; cache behavior) requires a running Jellyfin
server. **Docker is not available in this sandbox**, so a live Jellyfin instance could not
be started here. Everything that could be verified without it was verified (sections 1–3);
the full scenario is scripted and ready:

- `tests/integration/docker-compose.yml` — Jellyfin 10.11 + alignment service + seeded
  music volume.
- `tests/integration/seed_music.sh` — seeds 5 public-domain-audio tracks (synthetic tones,
  no copyrighted audio) with the requested matrix:
  1. Rick Astley – *Never Gonna Give You Up* (on LRCLIB, synced → expect `.lrc`)
  2. New Order – *Blue Monday* (on LRCLIB, synced → expect `.lrc`)
  3. Zxqw Blorptastic – *Nonexistent Song XYZ* (not on LRCLIB, lyrics.ovh miss → expect
     miss + retry recorded in cache)
  4. Coldplay – *Yellow* (plain via lyrics.ovh → expect `.txt`, or `.lrc` when the
     alignment service is enabled)
  5. Rick Astley – *Together Forever* (on LRCLIB → expect `.lrc`)
- `tests/integration/run_test.sh` — performs the exact checks: task discovery + run,
  sidecar existence per kind, `Synced` status, retry scheduling for the unknown track,
  `GET /Audio/{id}/Lyrics` payload, manual `POST /MultiLyrics/Search/{id}?source=LyricsOvh`
  override.

To execute (any Docker host):

```bash
cd tests/integration
./seed_music.sh
./install_plugin.sh            # publishes the plugin and stages it in ./plugin
docker compose up -d --build
./run_test.sh
```

To exercise the 12.x line instead:

```bash
JELLYFIN_TARGET=jf12 JELLYFIN_IMAGE=jellyfin/jellyfin:12 ./install_plugin.sh
JELLYFIN_IMAGE=jellyfin/jellyfin:12 docker compose up -d --build
```

CI validates both compose files (`docker compose config -q`) but does not run this suite; it
still needs a host with a Docker daemon.

Jellyfin Web synced-highlighting check (manual, after `run_test.sh` passes): play
track 1 in the web client, open the lyrics pane, confirm the active line highlights and
advances; play track 4 to confirm plain (static) display.

## 5. Testing checklist mapping

| Checklist item | State |
| --- | --- |
| Plugin loads and settings page renders on target Jellyfin version | 📄 built against verified 10.11 abstractions, page served via verified `IHasWebPages` mechanism; ⏳ visual confirm via integration run |
| LRCLIB writes `.lrc`; lyrics.ovh writes `.txt` | ✅ source logic + unit tests; ⏳ end-to-end via `run_test.sh` |
| Genius off by default; enabling requires acknowledgement | ✅ enforced in `GeniusSource.IsEnabled`, queue filter, config-page validation; unit-covered indirectly (IsEnabled guard logic) |
| Plain-only + no alignment = `.txt`; with alignment = `.lrc` | ✅ `FetchLyricsTask`/controller flow; LRC builder smoke-tested |
| Manual source override bypasses queue | ✅ `SearchAsync(forceSource)` + API; ⏳ end-to-end |
| Cache records misses and respects retry schedule | ✅ unit-tested with real JSON file persistence |
| `GET /Audio/{id}/Lyrics` returns sidecar; Web highlights synced lines | 📄 endpoint + resolver verified in Jellyfin source; ⏳ live via `run_test.sh` |
| No tokens or lyric text in logs | ✅ audited (re-audited on final pass): log calls carry only source names, status codes, counts, item titles and error class names — grep for `GeniusToken`, lyric bodies in `*.cs` returns only assignment/handler code, never `Log*` calls |

## 6. Deliverable map

| Requested | Where |
| --- | --- |
| Full source tree (exact filenames) | `Jellyfin.Plugin.MultiLyrics/` (+ small documented additions: `Constants.cs`, `Core/*`, `Lyrics/*`, `Api/Dtos.cs`) |
| Compiled, installable artifacts | `jellyfin-plugin-multi-lyrics-artifacts.zip` (sibling of this folder): `jf10-net9.0/` (Jellyfin 10.11) and `jf12-net10.0/` (Jellyfin 12), each with `Jellyfin.Plugin.MultiLyrics.dll` + `deps.json` + `pdb` + `HtmlAgilityPack.dll`. Curated from `dotnet publish`: server-provided assemblies (`MediaBrowser.*`, `Jellyfin.*`, EF Core, …) deliberately NOT shipped — copying the host's abstractions into the plugin folder causes TypeLoadException (Felitendo issue #55); only the genuine third-party dependency `HtmlAgilityPack` travels with the plugin. Both DLLs verified built: net9.0 143,872 B / net10.0 142,848 B. |
| build.yaml with new GUID, targetAbi, framework | `Jellyfin.Plugin.MultiLyrics/build.yaml` — GUID `7c1e4a9e-52b8-4c2f-9a3d-6e8f1b2c4d5e`, `targetAbi 10.11.0.0`, `framework net9.0` |
| Settings page with validation + legal warnings | `Configuration/configPage.html` (ToS gating, numeric clamps, Genius/alignment validation) |
| Unit tests | `Jellyfin.Plugin.MultiLyrics.Tests/` (73 green) |
| Docker Compose integration test | `tests/integration/` (scripted; Docker unavailable in sandbox) |
| README | `README.md` |
| Verification report | this file |
| Alignment stub | `alignment-service/` (FastAPI app.py, Dockerfile, docker-compose.yml, requirements.txt) |
| Fork-or-extend decision | Extended the *pattern*, not the code: official template structure + Felitendo's dual-target csproj and rate-limit lessons (credited). Multi-source queue/cache/API is new code — a fork of Felitendo's single-source plugin would have carried its LRCLIB-only assumptions into every source. |
