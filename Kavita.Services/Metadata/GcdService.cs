using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services.Metadata;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Entities.Enums.KavitaPlus;
using Kavita.Models.Metadata;
using Kavita.Models.Parser;
using Kavita.Services.Extensions;

namespace Kavita.Services.Metadata;

public sealed class GcdService(HttpClient client, Func<CancellationToken, Task<string?>> authorization,
    string storePath, TimeProvider clock) : IGcdService, IDisposable
{
    private readonly ComicVineStore _store = new(storePath);
    private readonly SemaphoreSlim _gate = new(1);
    private const string Root = "https://www.comics.org/api/";
    private const int MinuteLimit = 25;
    private const int DayLimit = 2000;

    private static string Field(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var field)
            || field.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return "";
        var text = field.ToString().Trim();
        return text is "?" or "None" or "[no title indexed]" ? "" : text;
    }

    private static JsonElement[] Items(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray() : [];

    private static int? Id(JsonElement obj, string field, bool issue) =>
        GcdReference.TryParse(Field(obj, field)) is {} link && link.Issue == issue ? link.Id : null;

    private static string Segment(string value)
    {
        if (value.Length is 0 or > 200 || value is "." or ".." || value.Any(char.IsControl)
            || value.IndexOfAny(['/', '\\']) >= 0) throw new KavitaException("Enter a GCD title or catalog link.");
        return Uri.EscapeDataString(value);
    }

    private ComicVineStore.State State(string auth)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth)));
        var state = _store.ReadState();
        if (state?.Fingerprint == fingerprint) return state;
        var changed = new ComicVineStore.State(fingerprint, state?.Requests ?? [], DateTimeOffset.MinValue, null, null, null);
        _store.WriteState(changed, clearCache: true);
        return changed;
    }

    private static DateTimeOffset RetryAt(ComicVineStore.State state, DateTimeOffset now)
    {
        var day = state.Requests.Where(t => t > now.AddDays(-1)).ToArray();
        var minute = day.Where(t => t > now.AddMinutes(-1)).ToArray();
        return new[] { state.BlockedUntil, day.Length >= DayLimit ? day[0].AddDays(1) : now,
            minute.Length >= MinuteLimit ? minute[0].AddMinutes(1) : now }.Max();
    }

    public async Task<GcdHealthDto> GetHealthAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var auth = await authorization(ct);
            if (auth == null) return new(false, 0, 0, null, null, null, null);
            var state = State(auth);
            var now = clock.GetUtcNow();
            var retry = RetryAt(state, now);
            return new(true, state.Requests.Count(t => t > now.AddMinutes(-1)), state.Requests.Count(t => t > now.AddDays(-1)),
                state.LastSuccess, state.LastFailure, state.Failure, retry > now ? retry : null);
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonElement?> Read(string path, CancellationToken ct, bool cached = true)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var auth = await authorization(ct) ?? throw new KavitaException("GCD requires an active Kavita+ license and saved GCD credentials.");
            var state = State(auth);
            var now = clock.GetUtcNow();
            if (cached && _store.Read(path, now) is {} hit) return hit;
            if (RetryAt(state, now) > now) throw new KavitaException("GCD requests are paused. Check provider health for the retry time.");
            state = state with { Requests = state.Requests.Where(t => t > now.AddDays(-1)).Append(now).ToArray() };
            _store.WriteState(state);
            using var request = new HttpRequestMessage(HttpMethod.Get, Root + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("KavitaRPM/1.0");
            try
            {
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                {
                    var retry = response.Headers.RetryAfter;
                    var until = retry?.Date ?? now + (retry?.Delta ?? (response.StatusCode == HttpStatusCode.TooManyRequests
                        ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(30)));
                    var failure = $"GCD returned HTTP {(int)response.StatusCode}.";
                    _store.WriteState(state with { LastFailure = now, Failure = failure, BlockedUntil = until });
                    throw new KavitaException(failure);
                }
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var value = document.RootElement.Clone();
                if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
                _store.WriteState(state with { LastSuccess = now, Failure = null });
                if (cached) _store.Write(path, value, now.AddHours(24), 1024);
                return value;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                _store.WriteState(state with { LastFailure = now, Failure = "GCD request failed.", BlockedUntil = now.AddSeconds(30) });
                throw new KavitaException("GCD request failed.");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try { return await Read("series/6139/", ct, cached: false) is {} row && Id(row, "api_url", false) == 6139; }
        catch (KavitaException) { return false; }
    }

    // Rebuild page URLs locally. Never forward Basic credentials to a next link from a response.
    private async Task<(List<JsonElement> Rows, bool Complete)> Search(string path, CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await Read(page == 1 ? path : $"{path}?page={page}", ct);
            if (result == null) return (rows, false);
            rows.AddRange(Items(result.Value, "results"));
            if (Field(result.Value, "next").Length == 0)
                return (rows, int.TryParse(Field(result.Value, "count"), out var count) && count == rows.Count);
        }
        return (rows, false);
    }

    private static DateTime? Year(JsonElement obj, string field) =>
        int.TryParse(Field(obj, field), out var year) && year is > 0 and <= 9999
            ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;

    private static ExternalSeriesDetailDto? Detail(JsonElement row)
    {
        if (Id(row, "api_url", false) is not {} id || Field(row, "name").Length == 0) return null;
        return new() { GcdId = id, Name = Field(row, "name"), SiteUrl = $"https://www.comics.org/series/{id}/",
            CatalogLanguage = Field(row, "language"), CatalogCountry = Field(row, "country"),
            StartDate = Year(row, "year_began"), EndDate = Year(row, "year_ended"),
            Chapters = Items(row, "active_issues").Length, Genres = [], Staff = [], Tags = [],
            Provider = ScrobbleProvider.Kavita, PlusMediaFormat = PlusMediaFormat.Comic };
    }

    public async Task<ExternalSeriesDetailDto?> GetVolumeAsync(int volumeId, CancellationToken ct = default)
    {
        if (volumeId <= 0 || await Read($"series/{volumeId}/", ct) is not {} row || Detail(row) is not {} detail
            || detail.GcdId != volumeId) return null;
        detail.Publisher = await Publisher(row, ct);
        return detail;
    }

    private async Task<string?> Publisher(JsonElement row, CancellationToken ct)
    {
        if (Uri.TryCreate(Field(row, "publisher"), UriKind.Absolute, out var publisher)
            && publisher.AbsolutePath.Trim('/').Split('/') is ["api", "publisher", var raw]
            && int.TryParse(raw, out var id) && id > 0 && await Read($"publisher/{id}/", ct) is {} record)
            return Field(record, "name");
        return null;
    }

    public async Task<MatchSeriesResultDto> SearchVolumesAsync(string query, CancellationToken ct = default)
    {
        var matches = new MatchSeriesResultDto { Provider = MetadataProvider.Gcd };
        query = query.Trim();
        if (GcdReference.TryParse(query) is {} link)
        {
            int? series = link.Id;
            if (link.Issue)
                series = await Read($"issue/{link.Id}/", ct) is {} issue && Id(issue, "api_url", true) == link.Id
                    ? Id(issue, "series", false) : null;
            if (series.HasValue && await GetVolumeAsync(series.Value, ct) is {} detail)
                matches.Matches.Add(new ExternalSeriesMatchDto { Series = detail });
            return matches;
        }
        if (query.StartsWith("gcd:", StringComparison.OrdinalIgnoreCase)) throw new KavitaException("Enter gcd:series:ID or gcd:issue:ID.");
        var (rows, _) = await Search($"series/name/{Segment(query)}/", ct);
        foreach (var row in rows)
        {
            if (Detail(row) is not {} detail) continue;
            // Resolve publisher links on a bounded shortlist. Always resolve the selected series.
            if (matches.Matches.Count < 8) detail.Publisher = await Publisher(row, ct);
            matches.Matches.Add(new ExternalSeriesMatchDto { Series = detail });
        }
        return matches;
    }

    public async Task<ExternalSeriesDetailDto?> FindVolumeAsync(string title, int? year, string? publisher, CancellationToken ct = default)
    {
        if (!year.HasValue && string.IsNullOrWhiteSpace(publisher)) return null;
        try
        {
            var path = $"series/name/{Segment(title)}/" + (year.HasValue ? $"year/{year}/" : "");
            var (rows, complete) = await Search(path, ct);
            if (!complete) return null;
            var candidates = rows.Select(Detail).OfType<ExternalSeriesDetailDto>().Where(s =>
                string.Equals(s.Name, title, StringComparison.OrdinalIgnoreCase) && (!year.HasValue || s.StartDate?.Year == year)).ToList();
            if (candidates.Count != 1) return null;
            var detail = await GetVolumeAsync(candidates[0].GcdId!.Value, ct);
            return string.IsNullOrWhiteSpace(publisher) || string.Equals(publisher, detail?.Publisher, StringComparison.OrdinalIgnoreCase) ? detail : null;
        }
        catch (KavitaException) { return null; }
    }

    public async Task<IList<string>> GetCoversAsync(int id, bool issue, CancellationToken ct = default)
    {
        if (!issue)
        {
            if (await Read($"series/{id}/", ct) is not {} row || Id(row, "api_url", false) != id) return [];
            var first = Items(row, "active_issues").Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => GcdReference.TryParse(x.GetString())).FirstOrDefault(x => x is { Issue: true });
            if (first == null) return [];
            id = first.Id;
        }
        if (await Read($"issue/{id}/", ct) is not {} record || Id(record, "api_url", true) != id
            || !Uri.TryCreate(Field(record, "cover"), UriKind.Absolute, out var cover)
            || cover.Scheme != "https" || !cover.IsDefaultPort || cover.UserInfo.Length > 0
            || !cover.Host.EndsWith(".comics.org", StringComparison.OrdinalIgnoreCase)) return [];
        return [cover.AbsoluteUri];
    }

    public async Task<bool> EnrichAsync(IList<ParserInfo> infos, int? seriesId, CancellationToken ct = default)
    {
        if (await authorization(ct) == null) return false;
        try
        {
            var series = seriesId.HasValue ? await GetVolumeAsync(seriesId.Value, ct) : null;
            foreach (var info in infos.Where(i => !i.IsPlaceholder))
            {
                var links = (info.ComicInfo?.Web ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(GcdReference.TryParse).Where(x => x is { Issue: true }).Distinct().ToList();
                if (links.Count > 1) continue;
                var issueId = links.FirstOrDefault()?.Id ?? info.GcdId;
                string? expectedNumber = null;
                if (!issueId.HasValue)
                {
                    if (series == null || info.IsSpecialInfo()) continue;
                    var label = info.ComicInfo?.Number?.Trim();
                    if (string.IsNullOrEmpty(label))
                    {
                        label = info.Chapters;
                        if (!decimal.TryParse(label, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || n <= 0) continue;
                    }
                    expectedNumber = label;
                    var (rows, complete) = await Search($"series/name/{Segment(series.Name)}/issue/{Segment(label)}/", ct);
                    if (!complete) continue;
                    var matches = rows.Where(r => Id(r, "series", false) == seriesId).ToList();
                    if (matches.Count != 1) continue;
                    issueId = Id(matches[0], "api_url", true);
                }
                if (!issueId.HasValue || await Read($"issue/{issueId}/", ct) is not {} record
                    || Id(record, "api_url", true) != issueId || Id(record, "series", false) is not {} parent
                    || (expectedNumber != null && Field(record, "number") != expectedNumber)
                    || (!info.IsSpecialInfo() && seriesId.HasValue && parent != seriesId)) continue;
                info.GcdId = issueId;
                var comic = info.ComicInfo ??= new ComicInfo();
                ApplyIssue(comic, record);
                if (series?.GcdId != parent) continue;
                if (string.IsNullOrWhiteSpace(comic.Publisher)) comic.Publisher = series.Publisher ?? "";
                if (string.IsNullOrWhiteSpace(comic.LanguageISO)) comic.LanguageISO = series.CatalogLanguage ?? "";
            }
        }
        catch (KavitaException) { /* Keep local metadata on quota or provider failures. */ }
        return true;
    }

    internal static void ApplyIssue(ComicInfo comic, JsonElement issue)
    {
        var stories = Items(issue, "story_set");
        var interior = stories.Where(s => Field(s, "type") == "comic story").ToArray();
        var covers = stories.Where(s => Field(s, "type") == "cover").ToArray();
        static string Credits(JsonElement[] rows, string field) => string.Join(", ", rows.SelectMany(s => Field(s, field)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Where(s => s is not ("?" or "None")).Distinct(StringComparer.OrdinalIgnoreCase));
        static string Fill(string? local, string remote) => string.IsNullOrWhiteSpace(local) ? remote : local;
        comic.Title = Fill(comic.Title, Field(issue, "title"));
        if (interior.Length == 1) comic.Title = Fill(comic.Title, Field(interior[0], "title"));
        comic.Summary = Fill(comic.Summary, string.Join("\n\n", interior.Select(s => Field(s, "synopsis")).Where(s => s.Length > 0)));
        comic.Writer = Fill(comic.Writer, Credits(interior, "script"));
        comic.Penciller = Fill(comic.Penciller, Credits(interior, "pencils"));
        comic.Inker = Fill(comic.Inker, Credits(interior, "inks"));
        comic.Colorist = Fill(comic.Colorist, Credits(interior, "colors"));
        comic.Letterer = Fill(comic.Letterer, Credits(interior, "letters"));
        comic.Editor = Fill(comic.Editor, Credits(interior.Append(issue).ToArray(), "editing"));
        comic.CoverArtist = Fill(comic.CoverArtist, Credits(covers, "pencils"));
        comic.Characters = Fill(comic.Characters, Credits(interior, "characters"));
        comic.Genre = Fill(comic.Genre, Credits(interior, "genre"));
        comic.Tags = Fill(comic.Tags, Credits(interior.Append(issue).ToArray(), "keywords"));
        comic.Isbn = Fill(comic.Isbn, Field(issue, "isbn"));
        comic.GTIN = Fill(comic.GTIN, Field(issue, "barcode"));
        if (Id(issue, "api_url", true) is {} id)
            comic.Web = string.Join(',', (comic.Web ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append($"https://www.comics.org/issue/{id}/").Distinct());
        if (comic.Year == 0 && comic.Month == 0 && comic.Day == 0)
        {
            foreach (var field in new[] { "on_sale_date", "key_date" })
            {
                var raw = Field(issue, field);
                if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                { comic.Year = date.Year; comic.Month = date.Month; comic.Day = date.Day; break; }
            }
        }
    }

    public void Dispose() { client.Dispose(); _gate.Dispose(); _store.Dispose(); }
}
