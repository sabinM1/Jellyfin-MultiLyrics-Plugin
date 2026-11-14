# Multi-Lyrics for Jellyfin

A Jellyfin plugin that fetches lyrics for your music from **multiple sources in a
configurable priority order**, always tells you whether a result is **Synced**, **Plain**
or **Instrumental**, and supports **manual per-track source selection**.

Lyrics are **ephemeral by design**: fetched on demand, kept in memory for the track being played
only, and dropped when the song changes, when playback stops or when the server restarts.
Nothing is written to your library or to Jellyfin's metadata folder, so a
library scan never picks up plugin output and there is nothing to clean up.

Works with Jellyfin **10.11.x** (`.NET 9` build) and **12.x** (`.NET 10` build).

```
Client asks for lyrics → query sources in priority order (LRCLIB, lyrics.ovh, Tekstowo, Genius)
  → optionally align plain text via companion service → hold in memory (current track only)
  → Jellyfin serves GET /Audio/{id}/Lyrics → client parses and displays
  → the entry is dropped on the next song, on a miss, when playback stops (or after 2 h)
```

## Sources

| Source | Synced? | Key needed? | Default | Notes |
| --- | --- | --- | --- | --- |
| LRCLIB (lrclib.net) | ✅ synced (+word-level where available) | no | **always enabled, primary** | `GET /api/get` + `/api/search`, duration filtering; the toggle is locked |
| lyrics.ovh | ❌ plain | no | enabled | `GET /v1/{artist}/{title}` |
| Tekstowo.pl | ❌ plain | no | enabled | unofficial HTML scrape of song pages; robots.txt permits song pages |
| Genius | ❌ plain, by scraping the song page | optional | **OFF** | the official API never returns lyric text or timestamps; scraping violates Genius ToS → explicit acknowledgement required. Without a token the public genius.com search is used |

The plugin never bundles, logs or ships lyric text. Tokens are never logged.

## Installation

1. Get `Jellyfin.Plugin.MultiLyrics.dll` (CI artifact or build yourself — see
   [Development](#development)). The same source builds both Jellyfin lines:
   - `dotnet build -c Release` → **Jellyfin 10.11** artifact (net9.0)
   - `dotnet build -c Release -p:JellyfinTarget=jf12` → **Jellyfin 12** artifact (net10.0)
2. Copy the DLL into `<jellyfin-data>/plugins/` (Dashboard → Plugins → Open plugins
   directory), restart Jellyfin.
3. Open **Dashboard → Plugins → Multi-Lyrics** to configure. The settings page is embedded
   in the DLL; it is served at `/web/configurationpage?name=Multi-Lyrics`.

### Install from a plugin repository (catalog)

A tagged release (`.github/workflows/release.yml`) publishes two zips plus a generated
`manifest.json` whose `sourceUrl` and MD5 `checksum` match the shipped assets. Add one of these
URLs under *Dashboard → Plugins → Repositories*:

```
https://raw.githubusercontent.com/sabinM1/Jellyfin-MultiLyrics-Plugin/master/manifest.json
https://github.com/sabinM1/Jellyfin-MultiLyrics-Plugin/releases/latest/download/manifest.json
```

Both carry every released version, so the catalog shows a release history. The manifest is
generated, never hand-edited:

```bash
python scripts/gen_manifest.py --version <version> \
  --base-url https://github.com/sabinM1/Jellyfin-MultiLyrics-Plugin/releases/download/v<version> \
  --merge-existing manifest.json \
  --output manifest.json \
  artifacts/multi-lyrics_<version>_jf10.zip=10.11.0.0 \
  artifacts/multi-lyrics_<version>_jf12.zip=12.0.0.0
python scripts/validate_project.py    # strict: fails on placeholder URLs/checksums
```

`--merge-existing` keeps every previously published version. The release workflow does this
automatically: it merges onto the manifest currently served by `master`, attaches the result to
the release, and commits it back to `master`. Jellyfin installs the highest version whose
`targetAbi` matches your server; older entries stay available.

> `targetAbi` is `10.11.0.0` for the `jf10` artifact (loads on the whole 10.11.x line; it is
> built against the `10.11.0` abstraction floor, like the official template recommends) and
> `12.0.0.0` for the `jf12` artifact (net10.0).

## Configuration reference

Every setting has a working default, so a fresh install fetches lyrics without any
configuration. The single exception is Genius: the source stays **off** until you enable it and
tick the Terms-of-Service acknowledgement, and its client access token is optional. The settings
page has a **Restore defaults** button that resets the whole page (priority, per-source enables
and *Prefer synced lyrics*) and stores the result immediately.

### Shown in the settings page

| Setting | Default | Meaning |
| --- | --- | --- |
| Source priority | LRCLIB 1, lyrics.ovh 2, Tekstowo 3, Genius 4 | One priority number per source (1 = asked first). Every number must be used exactly once; duplicates are rejected with a visible error. A synced result ends the search, a plain result is kept as a fallback while later sources are tried (see *Prefer synced lyrics*) |
| Prefer synced lyrics | on | On: a synced result wins even when a higher-priority source returned plain text. Off: the order is literal — the first source that returns any lyrics wins, plain included. Turn it off to see (or verify) a plain-only source such as Genius or Tekstowo while LRCLIB is enabled |
| Per-source enable | lyrics.ovh/Tekstowo on; Genius off | Unticked sources are skipped entirely. **LRCLIB is always enabled**: its checkbox is locked, so a synced source is always in the queue |
| Genius ToS acknowledgement | off | Genius stays unusable until *“I understand this may violate Genius Terms of Service; personal use only”* is ticked. Enabling Genius means scraping the song page; there is no metadata-only mode |
| Genius token | empty | **Optional** client access token. With one, Genius' official search API is used; without one the public genius.com search is queried. **Stored in plain XML** — Jellyfin's plugin API has no secret store; file permissions on `<jellyfin-data>/config/Multi-Lyrics.xml` are your protection. Never logged and never echoed back into the settings page (the field shows `stored - leave blank to keep`). |

### Fixed recommended values (not shown in the settings page)

These keep Jellyfin's recommended behaviour and are no longer configurable from the UI. They
still live in the configuration file, so an install that previously changed one keeps its stored
value.

| Setting | Value | Meaning |
| --- | --- | --- |
| Duration tolerance | 10 s | Remote matches further than this from the track duration are rejected |
| Strict search | off | Title matching plus the duration window already reject covers and live versions; the artist is checked loosely |
| Retry schedule | `1,3,7,30` days | Per source, after that source missed the track: wait 1 day, then 3, 7, 30 (capped). A transient failure (timeout, 429/5xx, Cloudflare) waits 10 minutes instead |
| Delay between requests | 500 ms | Rate limiting; sources enforce their own minimums too (Tekstowo 1.5 s, LRCLIB 0.5 s) |
| Fetch on demand | on | A client asking for lyrics of a track that is not in memory triggers an immediate sweep (whole sweep bounded to 40 s, each source bounded to 8-10 s, at most two concurrent fetches) |
| Memory window | current track only, 2 h TTL | Nothing is written to disk; the next track's lyrics replace them, a miss drops them, and stopping playback drops them immediately. The TTL is only a safety net |
| Alignment service URL | empty | Companion Demucs + WhisperX service. Dormant: set `AlignmentServiceUrl` in the plugin XML if you run one; plain results stay plain text while it is empty |
| Allow private alignment host | off | Permit loopback/private addresses for the alignment service. Cloud metadata endpoints are always refused |

### Files

Configuration is persisted as `<jellyfin-data>/config/Multi-Lyrics.xml`. The retry cache
lives in `<jellyfin-data>/config/Multi-Lyrics/cache.json`.

## Synced vs plain

Every result — in memory, in the retry cache and in the status API — carries one of:

- **Synced**: line or word-level timestamps. Jellyfin Web highlights the current
  line and advances it during playback. Word-level enhanced LRC looks like
  `[00:12.34]<00:12.34>Word <00:12.80>by word`.
- **Plain**: no timestamps. Displayed as static text.
- **Instrumental**: LRCLIB flagged the track as instrumental. Nothing is stored except a
  marker in memory, so the track is not re-fetched while it stays in the window.

Priority logic: a plain result is kept as a fallback while the queue keeps looking for a
synced result from a later source; a synced result (or an instrumental flag) ends the sweep.

The **Prefer synced lyrics** switch changes that. With it off, the queue stops at the first
source that returns anything: `Genius` at priority 1 answers with plain text and LRCLIB is never
asked, so the order you typed is the order you get. Useful to check what a plain source actually
returned — the log line reports every source anyway, so both modes are diagnosable.

## How it runs

- **On demand**: the player's **Lyrics** button (`GET /Audio/{id}/Lyrics`) hits a decorator
  around Jellyfin's lyric manager. If Jellyfin already knows lyrics for the track (a lyric
  stream in its database, e.g. a file you added yourself) that wins; otherwise the plugin asks
  the sources in priority order and returns the result. The whole sweep is bounded to 40 s,
  each source to 8–10 s, and at most two sweeps run at once.
- **In memory only**: a fetched result is held in `EphemeralLyricsStore`, which keeps a single
  entry — **the track being resolved right now** — with a 2 h TTL. Moving to the next song
  replaces it; if that track has no lyrics, the previous ones are dropped as well; and stopping
  playback drops them too, so nothing is left for a track you are no longer playing. Nothing is
  written to disk and a restart leaves nothing behind. Asking again while the entry is still
  held is instant — check the log for
  `Multi-Lyrics: served <kind> lyrics for "<title>" from memory`.
- **Retry cache** (`cache.json`): per track — per-source attempts (success, kind, note,
  duration), winning source, and a **per-source** retry window. It holds **no lyric text**. A
  source that missed a track only blocks itself; the other sources keep being queried, so a track
  that only exists on one source is still found when another source answered 404. Misses escalate
  through the retry schedule (`1,3,7,30` days) per source, while a transient failure (timeout,
  429/5xx, Cloudflare challenge) waits only 10 minutes. Tracks whose metadata changed start over,
  and changing the source configuration (order, toggles, Genius token) or the plugin version
  clears every window at once. A track that a source solved is fetched again when its text is no
  longer in memory, because text is never persisted.
- **Jellyfin integration** (`ILyricProvider`): the plugin is a remote lyric provider, so the
  built-in lyrics UI (item → lyrics tab → remote search) can list candidates and fetch the one
  you pick. Jellyfin's own *Save lyrics with media* library option still applies to files it
  saves itself — the plugin does not write files.
- **Fixed recommended behaviour**: sidecar writing, overwrite rules, the scheduled task and its
  caps are gone. An alignment service can still be enabled by editing `AlignmentServiceUrl` in
  the plugin XML (see the configuration reference).

## Manual search API (admin only)

All endpoints require administrator privileges (`RequiresElevation` policy).

```bash
# Force a search with ONE source (bypasses enable flags and retry windows; Genius still
# requires its acknowledgement + token). Holds the result in memory and updates the cache.
curl -X POST 'http://server:8096/MultiLyrics/Search/<itemId>?source=LrcLib' \
  -H 'X-Emby-Token: <adminToken>'

# Run the whole configured queue manually
curl -X POST 'http://server:8096/MultiLyrics/Search/<itemId>' -H 'X-Emby-Token: <adminToken>'

# Which sources were tried, what each returned, whether the lyrics are in memory, sync status
curl 'http://server:8096/MultiLyrics/Status/<itemId>' -H 'X-Emby-Token: <adminToken>'

# List sources and their capability
curl 'http://server:8096/MultiLyrics/Sources' -H 'X-Emby-Token: <adminToken>'
```

`Status` response (excerpt):

```json
{
  "itemId": "...", "name": "...", "syncStatus": "Synced",
  "winningSource": "LrcLib", "aligned": false,
  "inMemory": true, "instrumental": false,
  "lastAttemptUtc": "...", "retryAfterUtc": null, "attemptCount": 0,
  "alignmentConfigured": false,
  "attempts": [
    { "source": "LrcLib", "success": true, "kind": "Synced", "message": "word-level synced" },
    { "source": "Genius", "success": false, "kind": "None", "message": "Genius 3 hit(s), none matched" }
  ]
}
```

The endpoint requires elevation, so call it from Dashboard → API Docs (or with an admin API
key); the settings page no longer has a built-in tester.

## Alignment companion service (optional)

> The alignment service is not exposed in the settings page. It stays dormant while
> `AlignmentServiceUrl` is empty; set that value in `<jellyfin-data>/config/Multi-Lyrics.xml`
> if you run the service.

A FastAPI Docker stub (`alignment-service/`) that turns plain text into **word-level .lrc**
using Demucs (optional vocal isolation) + WhisperX forced alignment. No Python/ML runs
inside the plugin.

```
POST /align          { "audioPath": "/music/x.flac", "text": "...", "language": null,
                       "separateVocals": true }   → { "lrc": "...", "error": null }
POST /align/submit   → 202 { "jobId": "..." }    (queued, serialized)
GET  /align/status/{jobId} → { "state": "done", "lrc": "...", "error": null }
GET  /health
```

Setup:

```bash
cd alignment-service
docker compose up -d --build     # mounts /music read-only, caches models in a volume
```

Then set *Alignment service URL* (e.g. `http://alignment:8787`) and, for LAN/Docker hosts,
enable *Allow private alignment host*.

Plugin-side guarantees: SSRF guard on the URL (cloud metadata refused even with the
private-host toggle; DNS resolved and every address checked), redirects disabled at
transport level, responses capped at 2 MB, text capped at 32 KB, submit+poll transport with
a synchronous fallback, 429 (busy) surfaces as a skip.

Service-side guarantees: jobs serialized (sync endpoint returns 429 while busy),
`audioPath` must resolve inside `MUSIC_ROOT` (mounted read-only), size caps on input.

## Known limitations

- **Word-level display is client-dependent.** Synced lyrics carry `[mm:ss.xx]` line stamps and
  optional `<mm:ss.xx>` word tags; only clients that render cues highlight words. Jellyfin Web
  highlights lines.
- **Nothing is cached across restarts.** Lyrics are held in memory, so a server restart (or the
  next song) means the track is fetched again. That is deliberate — no library or metadata
  writes — but it costs one source request per track per session.
- **Two tracks at a time.** Two concurrent on-demand sweeps are allowed; a client that
  prefetches a whole album queues behind them.
- **Alignment accuracy depends on the companion service** (Demucs + WhisperX) and may be
  poor for dense mixes, overlapping vocals or spoken-word tracks.
- **Duration tolerance and strict search only apply where the source exposes that data**: it
  is enforced for LRCLIB (which returns a duration) and for artist metadata where available.
  lyrics.ovh and Tekstowo return text without duration, so they cannot be
duration-filtered.
- **Genius never returns timestamps**, and scraping is best-effort (Cloudflare challenges
  are treated as a miss).
- **Title/artist matching is heuristic** (diacritics folded, punctuation stripped, substring
  matching); unusual titles can still produce a loose match.
- **Alignment service is optional** and single-job: a busy service rejects new synchronous
  requests with 429.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Player says "No lyrics found" | Check the log for the request: `on-demand <kind> via <source>` means it was fetched, `served ... from memory` means it was already held, and `no lyrics found for "<title>" in <LrcLib,LyricsOvh,...>` lists what was tried. If no `Multi-Lyrics` line appears at all, the decorator did not load — confirm `Loaded plugin: Multi-Lyrics <version>` at startup and restart |
| Lyrics disappear after a while | Expected: they live in memory only, one track at a time. Change song, stop playback or restart and they are gone; press play again and the track is fetched once more |
| Jellyfin 12 hangs after `Prepare system for possible migrations` at startup | 1.0.3.0 only. Its lyric manager decorator resolved the delivery services in the constructor, which closed a DI cycle with `ProviderManager`. Update to the current version |
| A song exists on LRCLIB but is not found | Titles with release qualifiers are handled since 1.0.4.0: sources are queried with `Carnage` instead of `Carnage (feat. Little Simz) (Original Mix)`. Check the `no lyrics found for "<title>" in <sources>` line; a forced re-search is `POST /MultiLyrics/Search/{itemId}?source=LrcLib` |
| Genius is in the priority list but never queried | Genius needs *Enable Genius* and the ToS acknowledgement. The log states which of those is missing |
| Synced lyrics not highlighted | Only clients that render LRC cues highlight; Jellyfin Web highlights lines. Word-level tags come from the alignment service, which is off unless configured |
| Nothing happens on a miss | A source's own retry window suppresses only that source. `GET /MultiLyrics/Status/{itemId}` shows the per-source notes (404, rate limit, Cloudflare challenge) and the retry date; `POST /MultiLyrics/Search/{itemId}?source=<name>` forces a fresh sweep. The finish line of every sweep names each source's verdict, e.g. `"Carnage" -> Genius: plain (scraped ...); LrcLib: LRCLIB 404; winner: Genius (Plain)` |
| Genius never returns text | Genius only produces lyrics by scraping, so a Cloudflare challenge is a miss and is never written. Without a client token the public search may be blocked — setting a token makes the search use the official API |
| No plugin lines in the log at all | The plugin has no startup line of its own. Jellyfin logs `Loaded plugin: Multi-Lyrics <version>` when it loads; lyric lines appear when a client requests them, because there is no scheduled task any more. If neither appears, the plugin did not load (look for a load error near the top of the log) |
| Settings page still shows the old layout | The page is embedded in `Jellyfin.Plugin.MultiLyrics.dll` and prints its build number, so compare that number with `Loaded plugin: Multi-Lyrics <version>`. If they differ, the browser served a cached copy — reload with Ctrl+F5. If both show an older version, the new build was not installed: check `config/plugins/` and the catalog, then restart |
| Tekstowo misses | Slug guessing can fail for unusual names; keep it late in the priority order |
| Alignment always skipped | Service URL empty, SSRF guard refused (see log), or the service returned 429 (busy) |
| Tokens? | Never logged; stored in the plain XML config file documented above |

## Legal & privacy

- No lyric text is bundled in this repository or its artifacts; test fixtures use synthetic
  text only.
- Genius scraping is OFF by default and gated behind an explicit acknowledgement.
- Tekstowo is an unofficial source; respect the site and keep the request delay.
- You are responsible for the alignment service only aligning music you have rights to.

## Development

```bash
# restore (needed when no NuGet source is configured globally)
dotnet restore Jellyfin.Plugin.MultiLyrics -s https://api.nuget.org/v3/index.json

# build (10.11 target, net9.0)
dotnet build Jellyfin.Plugin.MultiLyrics -c Release --no-restore
# build (12.x target, net10.0)
dotnet restore Jellyfin.Plugin.MultiLyrics -p:JellyfinTarget=jf12 -s https://api.nuget.org/v3/index.json
dotnet build Jellyfin.Plugin.MultiLyrics -c Release --no-restore -p:JellyfinTarget=jf12

# unit tests (net9.0; prefix with DOTNET_ROLL_FORWARD=Major when only a newer runtime exists)
dotnet test Jellyfin.Plugin.MultiLyrics.Tests -c Release

# alignment service logic tests (no ML dependencies required)
python -m pytest alignment-service/test_lrc.py -q

# repository metadata + packaging validation
python -m pip install pyyaml
python scripts/validate_project.py

# pack a plugin zip from a publish output
dotnet publish Jellyfin.Plugin.MultiLyrics -c Release -o artifacts/publish-jf10
python scripts/pack_plugin.py --publish-dir artifacts/publish-jf10 \
  --output artifacts/multi-lyrics_1.0.1.0_jf10.zip

# full integration test (needs docker)
cd tests/integration && ./seed_music.sh && ./install_plugin.sh \
  && docker compose up -d --build && ./run_test.sh
```

### Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request:

| Job | What it does |
| --- | --- |
| `plugin` (matrix `jf10`, `jf12`) | restore → build → publish for both Jellyfin lines (net9.0 / net10.0), validates metadata and packaging inputs, packs the zip, uploads it as a build artifact |
| `test` | `dotnet test` on net9.0 (137 tests) with TRX results uploaded |
| `alignment-service` | byte-compiles `app.py`, runs the LRC logic tests, validates both compose files |
| `metadata` | `scripts/validate_project.py`: build.yaml/Constants/csproj/manifest consistency, default-off legal gates, secret and `TODO(verify)` scans |

`.github/workflows/release.yml` runs on `v*` tags: builds both artifacts, fails if the tag
does not match `build.yaml`'s version, creates the GitHub release with both zips, regenerates
`manifest.json` with real `sourceUrl` + MD5 checksum values, validates it strictly and
attaches it to the release.

Project layout:

```
Jellyfin.Plugin.MultiLyrics/
  build.yaml                    # manifest (guid, targetAbi 10.11.0.0, framework net9.0)
  Plugin.cs                     # BasePlugin + IHasWebPages (embedded config page)
  PluginServiceRegistrator.cs   # DI: HTTP clients, sources, cache, queue, memory store
  Configuration/                # PluginConfiguration.cs + configPage.html
  Core/                         # TrackQuery, LyricResult, LrcParser (+word cues), priority, retry, duration
  Sources/                      # ILyricSource + 4 implementations
  Lyrics/                       # queue, ILyricProvider, ILyricManager decorator, memory store, DTO builders
  Storage/                      # ResultCache (JSON, metadata only) + EphemeralLyricsStore
  Alignment/                    # AlignmentClient + SsrfGuard
  Api/                          # MultiLyricsController + DTOs
Jellyfin.Plugin.MultiLyrics.Tests/   # 137 unit tests
alignment-service/              # FastAPI + Dockerfile + docker-compose.yml + test_lrc.py
tests/integration/              # docker-compose.yml + seed_music.sh + install_plugin.sh + run_test.sh
scripts/                        # validate_project.py, pack_plugin.py, gen_manifest.py
.github/workflows/              # ci.yml (build + validate) + release.yml (tag release)
manifest.json                   # plugin catalog manifest (regenerated on release)
```

## Credits

- Built on the [official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template)
  conventions; the dual-target (10.11 / 12.x) csproj pattern follows Felitendo's
  [jellyfin-plugin-lyrics](https://github.com/Felitendo/jellyfin-plugin-lyrics) (LRCLIB
  rate-limit handling insights). This plugin is a separate multi-source implementation, not
  a fork; upstreaming the cache/retry design back to that project is welcome.
- Lyric data: [LRCLIB](https://lrclib.net), [lyrics.ovh](https://lyrics.ovh),
  [Tekstowo](https://www.tekstowo.pl), [Genius](https://genius.com).
- Alignment pipeline modeled after Lyricarr's Demucs + WhisperX approach:
  `whisperx.load_model → transcribe → load_align_model → whisperx.align`, word segments
  merged into enhanced LRC lines.

## License

MIT — see [LICENSE](LICENSE).
