**Multi-Lyrics** — fetch lyrics for your Jellyfin library from several sources, in a priority
order you control, and serve them to the player from memory.

## Changes in 1.0.0.0

First public release. The 0.x headings below are the pre-release history that led here.

- **Multi-source lyrics, in an order you control.** LRCLIB (synced, always enabled), lyrics.ovh,
  Tekstowo and Genius are queried by priority. A synced result ends the search; a plain result is
  kept as a fallback while the remaining sources are tried.
- **Nothing is kept.** Lyrics live in memory for the track being played only — dropped on the next
  song, on a miss, when playback stops, and on restart. No files in your library or metadata folder.
- **You can see why a track missed.** `Synced` / `Plain` / `Instrumental` everywhere, and one log
  line per sweep naming each source's verdict and the winner.
- **Genius is optional and gated.** Off by default; enabling it means scraping the song page and
  requires the Terms-of-Service acknowledgement. The client access token is optional.
- **Settings page**: per-source toggles and priorities, *Prefer synced lyrics*, *Restore defaults*.
- **Admin API**: `POST /MultiLyrics/Search/{itemId}` and `GET /MultiLyrics/Status/{itemId}`.

## Changes in 0.15.0.0

- **The settings page styles its own controls.** The enable checkboxes and the priority dropdowns
  fell back to native controls next to themed ones: a bare white square instead of a checkbox, and
  a plain browser select with no arrow for three of the four sources. Both now look like the rest
  of the dashboard.
- **LRCLIB is permanently enabled.** It is the primary source of synced lyrics, so its checkbox is
  ticked, locked, and the queue always contains it — the plugin can no longer end up with no
  synced source at all.
- **Genius is scraped whenever it is enabled.** The *Search only* mode is gone: enabling Genius in
  the table is the whole decision, and the legal note appears the moment it is ticked. A
  `SearchOnly` value left in an older configuration is treated as `Scrape`.
- **The Genius client access token is optional.** Scraping the song page needs no token; without
  one the plugin searches the public genius.com endpoint instead of the official API. With a token
  the official API is used, which is a little more reliable.
- **Restore defaults now resets the whole page and saves immediately.** It used to repaint only the
  source table, left *Prefer synced lyrics* untouched and waited for a manual Save. It now resets
  that switch too and stores the result right away, so the page and the server cannot disagree.
- **Corrected the test count in these notes.** They claimed 102 tests; the suite is 137.

## Changes in 0.14.0.0

- **Saving the settings page no longer fails with a 500 on an unset Genius mode.** If the server
  handed back a Genius mode the page did not recognise, the dropdown rendered blank and the save
  posted `null`. The host could not convert that to the mode enum, rejected the whole request, and
  *none* of the page's settings were stored. The value is normalised now, and a blank or unknown
  mode falls back to "Off".
- **Lyrics are dropped when playback stops.** They only ever lived in memory, but without a
  stopping track they could sit there for the two-hour TTL; a stop now clears them immediately.
  Nothing is ever written to disk, and a restart still clears everything.
- **The configuration stamp carries the Genius mode again.** It contained the literal text
  `{(int)cfg.GeniusMode}`, so changing only the Genius mode did not reset the per-source retry
  windows and the new mode was ignored for tracks already missed.

## Changes in 0.13.0.0

- **You can make the priority order literal.** A synced result always beat a plain one, so with
  LRCLIB enabled a plain-only source could never be what you saw: Genius reported
  `plain (scraped …)` for every track and the player still showed LRCLIB text, which makes the
  scrape look broken. The new **Prefer synced lyrics** switch (on by default) keeps the old
  behaviour; turning it off makes the queue stop at the first source in your order that returns
  any lyrics, so Genius at priority 1 wins with plain text and LRCLIB is never asked.
- **The setting resets retry windows like any other source change**, so flipping it takes effect
  on the next track instead of waiting for a source's 1/3/7/30-day window to expire.

## Changes in 0.12.0.0

This release is about one question: *why did the plugin not find lyrics for this track?*

- **Every sweep now names each source's verdict.** Previously a sweep logged nothing at all when
  one source missed and another won — the normal case, because a synced result from LRCLIB beats a
  plain result from Genius. Genius in particular returned a bare "nothing", so a rejected token, a
  Cloudflare challenge, search-only mode and "Genius does not have this song" all looked the same.
  Every source now returns a reason and one log line per track shows the lot, e.g.
  `Multi-Lyrics: "Carnage (feat. Little Simz) (Original Mix)" -> Genius: plain (scraped …); LrcLib:
  LRCLIB 404; winner: Genius (Plain)`.
- **A track that was solved before is fetched again.** The cache recorded the win, and the skip
  check treated *any* recorded result as "already has lyrics". Since lyric text is never
  persisted, a track fetched in an earlier session — or evicted when you moved to the next song —
  could never be fetched again and answered "no lyrics" forever. Only misses gate a fetch now.
- **A miss is per source, not per track.** One source returning 404 used to suppress the whole
  sweep for 1/3/7/30 days, so a track present only on Genius stayed unfindable while LRCLIB was
  in its window. Each source keeps its own window, and a transient failure (timeout, HTTP 429/5xx,
  Cloudflare challenge) waits 10 minutes instead of a day.
- **Changing the sources clears every window.** A configuration or plugin-version change used to
  bypass the windows for one sweep only; tracks missed under the old settings stayed blocked
  afterwards. It now resets them for every track.
- **Genius matching is wider.** The search title (release qualifiers and guest credits removed) is
  compared as well as the raw title, `full_title` is accepted, a hit matches when any of its
  `artist_names` fits the track artist (collab pages are often owned by another act), and
  `artist_names` is read whether Genius sends a string or an array. A search that returns hits but
  no acceptable match is retried once without the artist.
- **The scraped text no longer starts with the credit banner.** Genius marks its
  "N Contributors / Song Lyrics" header `data-exclude-from-selection="true"` *inside* the first
  lyric container, so it was concatenated into the payload and the player showed it as the first
  lyric lines.
- **A refused token is reported as such.** Genius can return HTTP 200 with a failing `meta.status`;
  that is now checked and reported as `API 401 (token rejected)` rather than `0 hits`.

## Changes in 0.11.0.0

- **Lyrics are held for exactly one track.** The in-memory window kept the last 4 tracks for two
  hours; it now keeps only the track being resolved. Moving to the next song replaces them, and a
  track that comes back empty drops the previous lyrics too, so nothing lingers in RAM. A server
  restart leaves nothing behind, and nothing is ever written to the library or metadata folder.

- **A source you enable is queried immediately.** Every track that missed kept a retry window
  (1/3/7/30 days) per source, and that window outlived configuration changes — enabling Genius,
  reordering the queue or installing a fix still produced `no lyrics … every source is still
  inside its retry window`. The recorded source configuration (order, toggles, Genius mode/token)
  and the plugin version are now part of the sweep: when either changed, retry windows are ignored
  for that run.
- **A deleted lyric file no longer produces a 500.** Jellyfin keeps the lyric stream in its
  database after the `.lrc` is deleted, so `GET /Audio/{id}/Lyrics` failed with
  `Could not find file '…lrc'`. The plugin now logs
  `the lyrics Jellyfin has for "<title>" could not be read (the lyric file no longer exists)`
  and serves freshly fetched lyrics instead of propagating the error. Run *Scan Media Library*
  to drop the stale stream.

## Changes in 0.10.0.0

- **Genius works.** Its `/search` response was parsed with no key mapping: Genius answers with
  `response`, `hits`, `result`, `url`, `title` and `primary_artist`, while the properties were
  `Response`, `Hits`, `Result`, `Url`, `Title` and `PrimaryArtist`. Nothing bound, every hit was
  discarded by the artist check, and the source behaved as if Genius had no such song — which is
  why enabling it and putting it first changed nothing. The keys are now explicit, and a search
  that returns hits but no acceptable match logs that (Debug level).
- **LRCLIB maps its payload explicitly as well**, so it no longer depends on serializer
  case-sensitivity defaults.
- **Honest miss logging.** A source inside its retry window is not queried; it used to appear in
  `no lyrics found for "<title>" in <sources>` as though it had been tried. Skips are now listed
  separately, and a track whose sources are all still in their retry window gets a distinct line
  pointing at `POST /MultiLyrics/Search/{itemId}`.

## Changes in 0.9.0.0

- **Saving settings can no longer fail silently.** After posting the configuration the page reads
  it back and renders the server's copy, so an update the server did not store is visible straight
  away instead of only after the next restart. A rejected request prints the HTTP status in the
  page instead of leaving the form looking saved.
- **The settings page runs again on Jellyfin 12.** Its `<script>` sat outside the
  `data-role="page"` element. Jellyfin's view loader only executes an inline script it finds
  *inside* that element — it inserts the view through jQuery in that case, and through a plain
  `appendChild` otherwise, which never runs scripts. The page's JavaScript therefore never
  executed: no value was loaded from the server and the Save button had no handler at all, which
  is indistinguishable from "Save does nothing". The script now sits inside the page element and
  also initialises on DOM ready rather than relying only on the view manager's `viewshow` event.
- **The page initialises itself.** It no longer does everything inside the `viewshow` event of
  Jellyfin's view manager. It also initialises when the document is ready, and binds the buttons
  by property assignment so a second init cannot submit twice.
- `Dashboard.alert` and `Dashboard.processServerConfigurationUpdateResult` are optional, so a web
  client that does not expose them cannot abort a save.

## Changes in 0.8.0.0

- **The settings page states its own build.** It prints the version of the assembly it is
  embedded in, so a stale browser copy or a plugin that was never upgraded looks different from
  the current page at a glance. A validator check keeps the printed number equal to the assembly
  version.
- **Removed claims of behaviour that no longer exists.** The page said the plugin writes a sidecar
  per track and logs its own loaded line at startup. Nothing is written to disk since 0.7.0.0, and
  the startup version comes from Jellyfin (`Loaded plugin: Multi-Lyrics <version>`).

## Changes in 0.7.0.0

- **Lyrics are ephemeral.** Nothing is written to your library or to Jellyfin's metadata folder
  any more, and the scheduled task is gone. A track is fetched when a client clicks **Lyrics**
  and held in memory: an LRU of the last 4 tracks with a 2 h TTL. Playing the next song pushes
  the previous entry out. That means no files to clean up, no scan surprises, and no plugin
  output in your library — at the cost of one source request per track per session.
- **No lyric text on disk.** `cache.json` now keeps only per-source attempts, the winning source
  and the retry window. Existing cache files are rewritten without text on the next save, and a
  new test asserts that a winning result leaves no lyric text in the file.
- **Settings page rebuilt.** Priority is an explicit number per source (1..4, each used once,
  duplicates rejected with a visible error) next to an enable toggle. The alignment-service,
  files-and-matching, scheduled-task and track-status sections were removed; the values they
  controlled keep the recommended defaults.
- **Status API** now reports `inMemory` and `instrumental` instead of `.lrc`/`.txt` paths.

## Changes in 0.6.0.0

- **Tracks whose title carries release qualifiers are found.** The queue queried sources with the
  raw Jellyfin title, so `Carnage (feat. Little Simz) (Original Mix)` (Chase & Status) matched
  nothing. LRCLIB holds it as `Carnage`; verified live, the raw title gives `/api/get` **404** and
  `/api/search` **0 results**, while the cleaned title gives **200** with 2 809 characters of
  synced lyrics. Search terms now drop guest credits and release qualifiers (`feat`, `ft`,
  `remaster`, `edit`, `original mix`, `explicit`, a year, ...) and keep qualifiers that change the
  recording (`live`, `acoustic`, `remix`). Multi-artist values (`A/ B`, `A; B`) are reduced to the
  first act for lookup; `AC/DC` is untouched.
- **Read-only media mounts no longer lose lyrics.** All four sidecar writes failed with
  `Read-only file system` on a `:ro` library. A failed beside-media write now falls back to the
  item's metadata folder, which Jellyfin also scans for lyrics, and the file is indexed from there
  so `GET /Audio/{id}/Lyrics` still serves it. Logged once: *the media folder is not writable, so
  sidecars go to Jellyfin's metadata folder instead*.
- **A slow source can no longer eat the whole budget.** Per-source timeouts are 8-10 s (Tekstowo
  was 25 s, exactly the on-demand budget) and the budget is 40 s, so every source gets a turn.
- **Silent Genius exclusion is gone.** Genius needs four things (enabled, ToS acknowledged, token,
  mode not `Off`); the log now names the missing one, once. Each full miss also logs
  `no lyrics found for "<title>" in <sources>`.

## Changes in 0.5.0.0

- **Jellyfin 12 boots again with the plugin installed.** 0.4.0.0 could stall startup right after
  `Prepare system for possible migrations`, with the health check reporting `Server is still
  starting up` until the container was restarted without the plugin. Jellyfin constructs
  `ProviderManager` with `ILyricManager` as a dependency, and this plugin's delivery path needs
  `IProviderManager` back; resolving it inside the decorator's constructor closed that loop. The
  decorator now resolves its services on first use, so constructing it touches nothing.
- **Regression test added.** `DecoratorConstructionResolvesNoPluginServices` fails if the
  decorator ever resolves the delivery path again.

## Changes in 0.4.0.0

- **Lyrics actually reach the player now.** Jellyfin's `GET /Audio/{id}/Lyrics` only reads
  lyric streams already stored in its database, so a freshly written sidecar used to stay
  invisible. The plugin now registers a decorator for Jellyfin's lyric manager, which serves the
  sidecar straight from disk and, when a track has none, fetches it on demand from your sources.
  No library scan is needed.
- **Word-level timing is passed through.** `<mm:ss.xx>` word tags are converted into API cues,
  so clients that render word highlighting get them (Jellyfin Web still highlights lines).
- **Fewer wasted queries.** A track whose sidecar exists in the media or metadata folder is
  skipped, and a cached result whose file was deleted is rewritten from the cache instead of
  asking the sources again.
- **Metadata folder policy works end to end.** The folder is created on demand, its sidecars are
  detected, and the status API reports them.
- **Catalog manifest keeps its history.** Each release appends its entries, and the workflow
  publishes the merged manifest to the default branch for you.

## Changes in 0.3.0.0

- **Sidecars appear immediately.** After writing a `.lrc`/`.txt` the plugin now queues a
  metadata refresh for that item, so Jellyfin's Lyrics view stops saying "No lyrics found"
  until the next full scan.
- **On-demand search is visible in the log.** The lyric provider logs each request, its
  result count, and when it is disabled by the fetch-on-demand setting.
- **Correct version in the log.** The startup line reported `0.1.0.0` while Jellyfin reported
  the installed `0.2.0.0`; the assembly version is now pinned.

## Changes in 0.2.0.0

- **Musixmatch removed.** Musixmatch disabled self-service signup, so a new install could not
  obtain a key, and synced lyrics needed a paid plan regardless. Sources are now LRCLIB,
  lyrics.ovh, Tekstowo and Genius.
- **Every setting has a default.** The settings page falls back to the default when a stored
  value is missing or out of range, and has a *Restore defaults* button. Only Genius is left
  unconfigured on purpose (no token, off, unacknowledged).
- **Visible in the log.** A `Multi-Lyrics <version> loaded:` line is written at server startup
  and each run logs the enabled source list.

## Downloads

| File | Jellyfin | Framework |
| --- | --- | --- |
| `multi-lyrics_1.0.0.0_jf10.zip` | 10.11.x | net9.0 |
| `multi-lyrics_1.0.0.0_jf12.zip` | 12.x | net10.0 |

`manifest.json` (attached below) is the plugin-catalog manifest for this release.

## Install

**Via the plugin catalog (recommended)**

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/sabinM1/Jellyfin-MultiLyrics-Plugin/master/manifest.json`
2. Catalog → Metadata → **Multi-Lyrics** → Install, then restart Jellyfin.

**Manual**

1. Create `config/plugins/MultiLyrics_1.0.0.0/`
2. Extract the zip for your Jellyfin line into that folder (files flat, no subfolder)
3. Restart Jellyfin and confirm the plugin under Dashboard → Plugins.

## What it does

- Queries sources in a configurable priority order: **LRCLIB** (synced, always enabled),
  **lyrics.ovh** (plain), **Tekstowo** (plain), **Genius** (scraped, OFF by default).
- Serves what it finds: the plugin hooks Jellyfin's lyric manager, so a result is returned as
  soon as it is fetched, in memory, and never written to the library.
- Reports sync status honestly: `Synced` / `Plain` / `Instrumental` — never presents plain
  text as timed.
- Short-circuits on a synced hit, keeps a plain hit as a fallback for the rest of the queue.
- Holds the lyrics of the **current track only** in memory (2 h TTL): the next song replaces
  them and a miss drops them, so nothing is left on disk or in RAM after a song change.
- Per-source retry schedule (1 / 3 / 7 / 30 days) for sources that came back empty; a transient
  failure waits 10 minutes instead. The cache stores outcomes only, never lyric text.
- Admin-only API:
  `POST /MultiLyrics/Search/{itemId}?source=lrclib` (forces a fresh query) and
  `GET /MultiLyrics/Status/{itemId}` (per-source result, retry date, in-memory state).
- Optional companion service (`alignment-service/`, Demucs + WhisperX) turns plain lyrics
  into word-level `.lrc`; while it is unset, plain results stay plain.

## Configuration notes

- Genius is **off by default** on a fresh install.
- Genius text scraping is gated behind an explicit Terms-of-Service acknowledgement
  checkbox; leaving it unticked keeps Genius out of the query list.
- Tokens are stored in Jellyfin's plugin configuration XML on disk and are masked in the
  settings page. Prefer a dedicated, low-privilege API key.

## Legal

Genius scraping violates Genius's Terms of Service. It is disabled by default and is
intended for personal, local use only. Tekstowo is an unofficial scraper with the same
caveat. All lyrics remain the copyright of their respective rights holders — this plugin
only fetches them, ships no lyric text, and grants no licence to redistribute any.

## Known limitations

- Word-level timing is only as good as the alignment service; `.elrc` is not emitted,
  everything is written to `.lrc`.
- Word-by-word cue highlighting depends on the client.
- Duration filtering applies to LRCLIB only (the other sources expose no duration).
- Instrumental detection is heuristic, and non-Latin scripts are handled best-effort.

## Verification

`dotnet build` for net9.0 and net10.0 plus the unit-test suite (137 tests) run in CI on every
push. End-to-end runs against a live Jellyfin container are covered by
`tests/integration/`; see `VERIFICATION.md` for the current status of each checklist item.
