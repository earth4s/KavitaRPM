using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Kavita.API.Services.Metadata;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.Entities.Enums.KavitaPlus;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Models.Parser;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Metadata;

/// <summary>
/// Optional, direct Comic Vine enrichment for Comic Vine libraries. The parser stays offline;
/// this service runs before the scanner builds chapter metadata and applies field locks.
/// </summary>
public sealed class ComicVineService(
    HttpClient client, string? apiKey, ILogger<ComicVineService> logger, TimeProvider timeProvider,
    Func<CancellationToken, Task<string?>>? resolveApiKey = null)
    : IComicVineService, IDisposable
{
    private const string ApiBase = "https://comicvine.gamespot.com/api/";
    private const string IssueFields = "id,name,description,deck,issue_number,cover_date,store_date,volume,person_credits,character_credits,team_credits,location_credits,story_arc_credits,site_detail_url";
    private const string VolumeFields = "id,publisher";
    // Stay below the published 200 requests/resource/hour, even when all requests use one resource.
    internal const int RequestsPerHour = 180;
    private const int CacheCapacity = 1024;
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly Dictionary<string, (JsonElement Data, DateTimeOffset Expires)> _cache = new();
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    private string _credentialFingerprint = string.Empty;

    public async Task<bool> EnrichAsync(IList<ParserInfo> infos, LibraryType libraryType, bool enableMetadata,
        CancellationToken ct = default)
    {
        if (!enableMetadata || libraryType is not (LibraryType.ComicVine or LibraryType.Comic)) return false;
        var key = await GetApiKey(ct);
        if (string.IsNullOrWhiteSpace(key)) return false;

        foreach (var info in infos.Where(i => !i.IsPlaceholder))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EnrichIssue(info, key, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                // Never log the exception or request URI: Comic Vine authenticates in the query string.
                #pragma warning disable S6667 // Exception messages may contain the API key.
                logger.LogWarning("[ComicVine] Lookup failed ({FailureType}); keeping local metadata", ex.GetType().Name);
                #pragma warning restore S6667
            }
        }
        return true;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var key = await GetApiKey(ct);
        if (string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            return await GetResponse("volumes/", "limit=1&field_list=id", key, ct, useCache: false) != null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    private Task<string?> GetApiKey(CancellationToken ct) =>
        resolveApiKey == null ? Task.FromResult(apiKey) : resolveApiKey(ct);

    public async Task<MatchSeriesResultDto> SearchVolumesAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length is 0 or > 200) throw new KavitaException("Enter a comic title or Comic Vine volume ID.");
        var key = await RequireApiKey(ct);
        var result = new MatchSeriesResultDto { Provider = MetadataProvider.ComicVine };
        var directId = ParseId(query.StartsWith("comicvine:", StringComparison.OrdinalIgnoreCase) ? query[10..] : query);
        var (linkedId, isVolume) = ExternalIdParser.GetComicVineId(query);
        if (linkedId != null)
        {
            if (!isVolume) throw new KavitaException("Select a Comic Vine series/volume URL (4050), rather than an issue URL (4000).");
            directId = ParseId(linkedId);
        }

        if (directId.HasValue)
        {
            var volume = await GetVolumeAsync(directId.Value, ct);
            if (volume != null) result.Matches.Add(new ExternalSeriesMatchDto { Series = volume });
            return result;
        }
        if (Uri.TryCreate(query, UriKind.Absolute, out _))
            throw new KavitaException("Enter a Comic Vine series/volume URL, volume ID, or comic title.");

        var response = await GetResponse("search/",
            "resources=volume&limit=20&field_list=id,name,description,deck,start_year,publisher,count_of_issues,image,site_detail_url,aliases&query="
            + Uri.EscapeDataString(query), key, ct);
        if (response == null) throw ProviderUnavailable();
        foreach (var volume in Items(response.Value, "results"))
        {
            var detail = VolumeDetail(volume);
            if (detail != null) result.Matches.Add(new ExternalSeriesMatchDto { Series = detail });
        }
        return result;
    }

    public async Task<ExternalSeriesDetailDto?> GetVolumeAsync(int volumeId, CancellationToken ct = default)
    {
        if (volumeId <= 0) return null;
        var key = await RequireApiKey(ct);
        var response = await GetResponse($"volume/4050-{volumeId}/",
            "field_list=id,name,description,deck,start_year,publisher,count_of_issues,image,site_detail_url,aliases", key, ct);
        if (response == null) throw ProviderUnavailable();
        if (!response.Value.TryGetProperty("results", out var volume) || Int(volume, "id") != volumeId) return null;
        return VolumeDetail(volume);
    }

    private async Task<string> RequireApiKey(CancellationToken ct)
    {
        var key = await GetApiKey(ct);
        return string.IsNullOrWhiteSpace(key)
            ? throw new KavitaException("Configure your API key in Settings > Kavita+ > Meta API Connections > Comic Vine first.") : key;
    }

    private static KavitaException ProviderUnavailable() =>
        new("Comic Vine is unavailable or its request limit was reached. Check your API key or try again later.");

    private static ExternalSeriesDetailDto? VolumeDetail(JsonElement volume)
    {
        var id = Int(volume, "id");
        if (id is not > 0 || string.IsNullOrWhiteSpace(Text(volume, "name"))) return null;
        DateTime? start = int.TryParse(Text(volume, "start_year"), out var year) && year is > 0 and <= 9999
            ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;
        var publisher = volume.TryGetProperty("publisher", out var p) ? PlainText(Text(p, "name")) : string.Empty;
        var image = volume.TryGetProperty("image", out var i) ? SafeComicVineUrl(Text(i, "small_url")) : null;
        return new ExternalSeriesDetailDto
        {
            Name = PlainText(Text(volume, "name")), ComicVineId = id,
            Summary = PlainText(Fill(Text(volume, "description"), Text(volume, "deck"))),
            SiteUrl = SafeComicVineUrl(Text(volume, "site_detail_url")), CoverUrl = image,
            StartDate = start, Publisher = publisher, Chapters = Int(volume, "count_of_issues") ?? 0,
            Genres = [], Staff = [], Tags = [], Editions = [], Provider = ScrobbleProvider.Kavita,
            PlusMediaFormat = PlusMediaFormat.Comic,
            Synonyms = Text(volume, "aliases").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        };
    }

    private static string? SafeComicVineUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "comicvine.gamespot.com" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetLeftPart(UriPartial.Path) : null;

    private async Task EnrichIssue(ParserInfo info, string key, CancellationToken ct)
    {
        var issueId = ParseId(info.ComicVineId);
        var volumeId = ParseId(info.ComicVineSeriesId);
        if (issueId == null && volumeId == null) return;

        if (issueId == null)
        {
            // Never turn an unknown issue (the parser's default 0), a range, or a special into a guess.
            var explicitNumber = info.ComicInfo?.Number;
            var number = NormalizeNumber(string.IsNullOrWhiteSpace(explicitNumber) ? info.Chapters : explicitNumber);
            if (number == null || (number == "0" && string.IsNullOrWhiteSpace(explicitNumber))) return;

            var list = await GetResponse("issues/", $"filter={Uri.EscapeDataString($"volume:{volumeId},issue_number:{number}")}&limit=100&field_list=id,issue_number,volume", key, ct);
            if (list == null || Int(list.Value, "number_of_total_results") != 1) return;
            var matches = Items(list.Value, "results").ToList();
            if (matches.Count != 1 || NormalizeNumber(Text(matches[0], "issue_number")) != number
                || NestedId(matches[0], "volume") != volumeId) return;
            issueId = Int(matches[0], "id");
            if (issueId == null || issueId <= 0) return;
        }

        var response = await GetResponse($"issue/4000-{issueId}/", $"field_list={IssueFields}", key, ct);
        if (response == null || !response.Value.TryGetProperty("results", out var issue)
            || issue.ValueKind != JsonValueKind.Object || Int(issue, "id") != issueId) return;

        var returnedVolumeId = NestedId(issue, "volume");
        if (volumeId != null && volumeId != returnedVolumeId) return;

        JsonElement? volume = null;
        if (returnedVolumeId > 0)
        {
            var volumeResponse = await GetResponse($"volume/4050-{returnedVolumeId}/", $"field_list={VolumeFields}", key, ct);
            if (volumeResponse != null && volumeResponse.Value.TryGetProperty("results", out var result)
                && result.ValueKind == JsonValueKind.Object && Int(result, "id") == returnedVolumeId)
                volume = result;
        }

        var metadata = info.ComicInfo ??= new ComicInfo();
        ApplyMissingMetadata(metadata, issue, volume);
        info.ComicVineId = issueId.Value.ToString(CultureInfo.InvariantCulture);
        if (returnedVolumeId > 0)
            info.ComicVineSeriesId = returnedVolumeId.Value.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<JsonElement?> GetResponse(string path, string query, string key, CancellationToken ct,
        bool useCache = true)
    {
        var cacheKey = path + "?" + query;
        await _gate.WaitAsync(ct);
        try
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            if (_credentialFingerprint != fingerprint)
            {
                _credentialFingerprint = fingerprint;
                _cache.Clear();
                _blockedUntil = DateTimeOffset.MinValue;
                // Preserve request accounting across key changes, including the hourly budget.
            }
            var now = timeProvider.GetUtcNow();
            if (useCache && _cache.TryGetValue(cacheKey, out var cached) && cached.Expires > now) return cached.Data;
            if (now < _blockedUntil) return null;

            while (_requests.TryPeek(out var sent) && now - sent >= TimeSpan.FromHours(1)) _requests.Dequeue();
            if (_requests.Count >= RequestsPerHour)
            {
                _blockedUntil = _requests.Peek() + TimeSpan.FromHours(1);
                logger.LogInformation("[ComicVine] Hourly request budget reached; keeping local metadata until the next scan");
                return null;
            }

            var delay = _lastRequest + RequestSpacing - now;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, ct);
            now = timeProvider.GetUtcNow();
            _lastRequest = now;
            _requests.Enqueue(now);

            // Only fixed endpoint paths built from numeric IDs reach this method. The owned client has
            // no request logging handlers, redirects or cookies, so the API key cannot leak through them.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ApiBase + cacheKey + "&format=json&api_key=" + Uri.EscapeDataString(key));
            request.Headers.UserAgent.ParseAdd("KavitaRPM-ComicVine/1.0 (+https://github.com/earth4s/KavitaRPM)");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter;
                var retryAt = retryAfter?.Date ?? now + (retryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                _blockedUntil = retryAt < now + TimeSpan.FromMinutes(5) ? now + TimeSpan.FromMinutes(5) : retryAt;
                logger.LogWarning("[ComicVine] HTTP {StatusCode}; keeping local metadata and pausing lookups", (int)response.StatusCode);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "status_code") != 1)
            {
                // Error text is provider-controlled and may echo credentials. Only log its numeric code.
                _blockedUntil = now + TimeSpan.FromMinutes(5);
                logger.LogWarning("[ComicVine] API error {StatusCode}; keeping local metadata", Int(root, "status_code"));
                return null;
            }

            var data = root.Clone();
            if (_cache.Count >= CacheCapacity) _cache.Remove(_cache.Keys.First());
            _cache[cacheKey] = (data, now + CacheLifetime);
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) _blockedUntil = timeProvider.GetUtcNow() + TimeSpan.FromMinutes(5);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static void ApplyMissingMetadata(ComicInfo metadata, JsonElement issue, JsonElement? volume)
    {
        metadata.Title = Fill(metadata.Title, PlainText(Text(issue, "name")));
        metadata.Summary = Fill(metadata.Summary, PlainText(Fill(Text(issue, "description"), Text(issue, "deck"))));
        metadata.Writer = Fill(metadata.Writer, Credits(issue, "writer", "script"));
        metadata.Penciller = Fill(metadata.Penciller, Credits(issue, "penciler", "penciller", "pencils"));
        metadata.Inker = Fill(metadata.Inker, Credits(issue, "inker", "inks"));
        metadata.Colorist = Fill(metadata.Colorist, Credits(issue, "colorist", "colors"));
        metadata.Letterer = Fill(metadata.Letterer, Credits(issue, "letterer", "letters"));
        metadata.CoverArtist = Fill(metadata.CoverArtist, Credits(issue, "cover", "cover artist"));
        metadata.Editor = Fill(metadata.Editor, Credits(issue, "editor"));
        metadata.Characters = Fill(metadata.Characters, Names(issue, "character_credits"));
        metadata.Teams = Fill(metadata.Teams, Names(issue, "team_credits"));
        metadata.Locations = Fill(metadata.Locations, Names(issue, "location_credits"));
        metadata.StoryArc = Fill(metadata.StoryArc, Names(issue, "story_arc_credits"));
        if (volume.HasValue && volume.Value.TryGetProperty("publisher", out var publisher))
            metadata.Publisher = Fill(metadata.Publisher, PlainText(Text(publisher, "name")));

        if (metadata.Year == 0 && TryDate(issue, out var date))
        {
            metadata.Year = date.Year;
            metadata.Month = date.Month;
            metadata.Day = date.Day;
        }

        // Keep a visible source link alongside existing links for Comic Vine attribution.
        if (Uri.TryCreate(Text(issue, "site_detail_url"), UriKind.Absolute, out var source)
            && source.Scheme == Uri.UriSchemeHttps && source.Host == "comicvine.gamespot.com"
            && string.IsNullOrEmpty(source.UserInfo))
        {
            var url = source.GetLeftPart(UriPartial.Path);
            var links = SplitLinks(metadata.Web);
            metadata.Web = string.Join(",", links.Append(url).Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool TryDate(JsonElement issue, out DateTime date) =>
        DateTime.TryParseExact(Text(issue, "store_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        || DateTime.TryParseExact(Text(issue, "cover_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Fill(string? local, string? remote) => string.IsNullOrWhiteSpace(local) ? remote ?? string.Empty : local;

    internal static void FillFromExistingChapter(ComicInfo metadata, Chapter chapter)
    {
        metadata.Title = Fill(metadata.Title, chapter.TitleName);
        metadata.Summary = Fill(metadata.Summary, chapter.Summary);
        metadata.Writer = Fill(metadata.Writer, People(PersonRole.Writer));
        metadata.Penciller = Fill(metadata.Penciller, People(PersonRole.Penciller));
        metadata.Inker = Fill(metadata.Inker, People(PersonRole.Inker));
        metadata.Colorist = Fill(metadata.Colorist, People(PersonRole.Colorist));
        metadata.Letterer = Fill(metadata.Letterer, People(PersonRole.Letterer));
        metadata.CoverArtist = Fill(metadata.CoverArtist, People(PersonRole.CoverArtist));
        metadata.Editor = Fill(metadata.Editor, People(PersonRole.Editor));
        metadata.Publisher = Fill(metadata.Publisher, People(PersonRole.Publisher));
        metadata.Characters = Fill(metadata.Characters, People(PersonRole.Character));
        metadata.Teams = Fill(metadata.Teams, People(PersonRole.Team));
        metadata.Locations = Fill(metadata.Locations, People(PersonRole.Location));
        metadata.StoryArc = Fill(metadata.StoryArc, chapter.StoryArc);
        if (metadata.Year == 0 && chapter.ReleaseDate != default)
        {
            metadata.Year = chapter.ReleaseDate.Year;
            metadata.Month = chapter.ReleaseDate.Month;
            metadata.Day = chapter.ReleaseDate.Day;
        }

        var sources = SplitLinks(chapter.WebLinks)
            .Where(link => Uri.TryCreate(link, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "comicvine.gamespot.com" && string.IsNullOrEmpty(uri.UserInfo));
        metadata.Web = string.Join(",", SplitLinks(metadata.Web)
            .Concat(sources).Distinct(StringComparer.OrdinalIgnoreCase));

        string People(PersonRole role) => string.Join(", ", chapter.People.Where(person => person.Role == role)
            .Select(person => person.Person.Name).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Credits(JsonElement issue, params string[] roles) => string.Join(", ",
        Items(issue, "person_credits")
            .Where(person => Text(person, "role").Split(',', StringSplitOptions.TrimEntries)
                .Any(role => roles.Contains(role, StringComparer.OrdinalIgnoreCase)))
            .Select(person => PlainText(Text(person, "name")))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string[] SplitLinks(string links) =>
        links.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Names(JsonElement issue, string field) => string.Join(", ", Items(issue, field)
        .Select(item => PlainText(Text(item, "name")))
        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string PlainText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return HtmlEntity.DeEntitize(document.DocumentNode.InnerText).Trim();
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string field) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Text(JsonElement parent, string field) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static int? Int(JsonElement parent, string field) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id) ? id : null;

    private static int? NestedId(JsonElement parent, string field) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(field, out var value) ? Int(value, "id") : null;

    private static int? ParseId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private static string? NormalizeNumber(string? value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.############################", CultureInfo.InvariantCulture) : null;

    public void Dispose()
    {
        client.Dispose();
        _gate.Dispose();
    }
}
