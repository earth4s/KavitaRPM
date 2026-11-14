# Direct Comic Vine metadata

This fork can enrich a **Comic** library (the `ComicVine` library type, not
**Comic (Flexible)**) directly through the Comic Vine API. It does not require
Kavita+. `ComicVineParser` still reads local files and determines grouping; the
asynchronous enrichment runs in `ProcessSeries` before chapter metadata is saved.

## Enable

1. Sign in at <https://comicvine.gamespot.com/api/> and obtain your own API key.
2. Set `KAVITA_COMICVINE_API_KEY` in the Kavita server process environment and
   restart Kavita. With no key, no requests are made. Keep the key out of Git.
3. Enable metadata reading for the library and run a **forced library scan**.
   A normal incremental scan intentionally skips unchanged files.

For a systemd installation, use `systemctl edit kavita` (substitute your unit's
actual name) and add:

```ini
[Service]
Environment="KAVITA_COMICVINE_API_KEY=YOUR_API_KEY"
```

Then restart the service. Treat this setting as a secret and restrict who can read
the unit override. To disable direct lookups, remove the variable and restart.

## Matching

An explicit identifier is required. Supported inputs in an archive's
`ComicInfo.xml` are the same inputs already recognized by the parser:

- An issue URL in `Web`, for example
  `https://comicvine.gamespot.com/example/4000-100/`.
- An issue ID in `Notes`, using `Scraped metadata from ComicVine [CVDB100]`.
- A series/volume URL in `Web` (`4050-...`) together with a single numeric issue
  number from `Number` or the parsed filename.

Comic Vine calls a series/run a **volume** (`4050`); an individual issue uses
`4000`. The service queries `/issues/` with the volume ID and issue number when
only the volume ID is available, then requests `/issue/4000-ID/` and
`/volume/4050-ID/`. It rejects ambiguous results, mismatched IDs, and ranges.
An explicit `Number` of `0` is supported; an unknown parser-default `0` is skipped.
Title-only searches and nonnumeric issue labels are not implemented.

## Fields and precedence

The service fills missing titles, summaries, creator credits, publisher,
characters, teams, locations, story arcs and publication dates. It prefers the
store date, falling back to the cover date. Remote HTML is converted to plain text.

Existing embedded values take priority. The scanner's existing chapter and series
field locks still apply. Previously saved values are the final fallback for missing
fields while the file's Comic Vine IDs still match, so an outage does not erase
downloaded metadata on a forced rescan. Series names, issue numbers, volume grouping, completion
counts, and archive contents are not changed. Covers are still handled by the
existing local-cover workflow. A validated Comic Vine source URL is added to the
issue's web links for attribution.

## Requests and failures

- One singleton shares a bounded in-memory cache, request budget and request gate
  across concurrent scans. Successful responses are cached for 24 hours, with up
  to 1,024 entries. Restarting clears this cache and the request accounting.
- Requests are spaced at least 1.1 seconds apart and capped at 180 total requests
  in a rolling hour, below Comic Vine's published 200/resource/hour allowance.
  Exhausting the budget skips further lookups instead of holding a scan for an
  hour. Run another forced scan later to fill remaining metadata.
- HTTP/API/network failures pause new requests for at least five minutes; HTTP
  `Retry-After` is honored when longer. Local scanning continues.
- Requests time out after 30 seconds. API keys, request URLs and provider error
  bodies are excluded from this service's logging. Redirects and cookies are
  disabled for the dedicated client.
- The API key is sent only to the fixed HTTPS Comic Vine API host. No Kavita+
  license or proxy is involved in these requests. Existing Kavita+ features remain
  independently controlled by their settings.

No live API requests are needed for tests: `ComicVineServiceTests` uses a fake HTTP
handler and clock. Live validation requires a key supplied by the operator.

Comic Vine's API terms cover non-commercial use, attribution, request limits and
caching: <https://comicvine.gamespot.com/api/>.
