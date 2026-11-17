# Direct Comic Vine metadata

This fork can enrich **Comic** and **Comic (Flexible)** libraries directly through
the Comic Vine API. An active Kavita+ license is required to view Metadata Providers,
manage provider API keys, match series, and perform direct lookups. `ComicVineParser` still reads local files and determines grouping; the
asynchronous enrichment runs in `ProcessSeries` before chapter metadata is saved.

## Enable

1. Sign in at <https://comicvine.gamespot.com/api/> and obtain your own API key.
2. Activate your Kavita+ license, then as a Kavita administrator open
   **Settings > Kavita+ > Metadata Providers > Comic Vine**. Enter the
   key, select **Save API key**, then **Test connection**. Changes take effect on
   the next lookup without restarting. The test makes a small, uncached request
   directly to Comic Vine and uses the same request limits as scanning.
3. Enable metadata reading for the library and run a **forced library scan**.
   A normal incremental scan intentionally skips unchanged files.

Keys saved through Settings are protected using ASP.NET Core Data Protection and
stored in the server database. Only administrators can read credential status,
save/replace/remove a key or test the connection. Neither the settings response nor
the general server settings includes the key. The input is blank when revisiting
the page. Debug exports scrub the protected setting and the Data Protection keys.

**Replace API key** updates the credential immediately, clears cached responses and
the old credential's failure cooldown, and retains hourly request accounting.
**Remove saved key** deletes it; if an environment key is configured, that key then
becomes active. With neither credential configured, no requests are made.

An environment key remains supported for unattended installations. A saved key
takes priority over `KAVITA_COMICVINE_API_KEY`. For a systemd installation, use
`systemctl edit kavita` (substitute your unit's
actual name) and add:

```ini
[Service]
Environment="KAVITA_COMICVINE_API_KEY=YOUR_API_KEY"
```

Then restart the service. Treat this setting as a secret and restrict who can read
the unit override. To disable direct lookups entirely, remove any saved key in
Settings and remove the environment variable, then restart.

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
Use **Settings > Import > Matched Series** to search by title or select a Comic Vine
volume ID/URL and save a parent series match. Issue enrichment still requires a
unique issue identifier or a matched volume and numeric issue number; nonnumeric
issue labels are not implemented.

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
- The API key is sent only to the fixed HTTPS Comic Vine API host. Requests go
  directly to Comic Vine; Kavita's existing license service verifies Kavita+ access.
- Without an active Kavita+ license, credential endpoints and direct matching
  endpoints are restricted, and background scans cannot resolve saved or environment
  API keys. Saved keys remain protected in the database for later reactivation.
  Local file parsing and Specials folder grouping continue without a license.

No live API requests are needed for tests: `ComicVineServiceTests` uses a fake HTTP
handler and clock. Live validation requires a key supplied by the operator.

Comic Vine's API terms cover non-commercial use, attribution, request limits and
caching: <https://comicvine.gamespot.com/api/>.
