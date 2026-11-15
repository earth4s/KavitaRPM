using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Models.Parser;
using Kavita.Services.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kavita.Services.Tests;

public class ComicVineServiceTests
{
    private const string IssueJson = """
        {"status_code":1,"results":{
          "id":100,"name":"The &amp; Beginning","description":"<p>Issue summary</p>",
          "volume":{"id":200},"store_date":"2020-05-13","cover_date":"2020-07-01",
          "site_detail_url":"https://comicvine.gamespot.com/example/4000-100/",
          "person_credits":[{"name":"Writer One","role":"writer, editor"},
            {"name":"Artist One","role":"penciler, inker, cover"},
            {"name":"Colorist One","role":"colorist"},
            {"name":"Letterer One","role":"letterer"}],
          "character_credits":[{"name":"Hero"}],"team_credits":[{"name":"Heroes"}],
          "location_credits":[{"name":"Gotham"}],"story_arc_credits":[{"name":"First Arc"}]
        }}
        """;
    private const string VolumeJson = """
        {"status_code":1,"results":{"id":200,"publisher":{"name":"Example Publisher"}}}
        """;

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()
        {
            // Advancing clock avoids real waits while exercising the hourly request window.
            _now += TimeSpan.FromSeconds(2);
            return _now;
        }
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode? FailureStatus { get; set; }
        public TaskCompletionSource? ResponseGate { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Equal("comicvine.gamespot.com", request.RequestUri!.Host);
            Assert.Contains("api_key=test-key", request.RequestUri.Query);
            Assert.Contains("KavitaRPM-ComicVine", request.Headers.UserAgent.ToString());
            if (ResponseGate != null) await ResponseGate.Task.WaitAsync(cancellationToken);
            return FailureStatus.HasValue ? new HttpResponseMessage(FailureStatus.Value)
                : respond?.Invoke(request) ?? Json(request.RequestUri.AbsolutePath.Contains("/volume/") ? VolumeJson : IssueJson);
        }
    }

    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    internal static ComicVineService Service(Handler handler, Clock? clock = null, string? key = "test-key") =>
        new(new HttpClient(handler), key, NullLogger<ComicVineService>.Instance, clock ?? new Clock());

    private static ParserInfo Info(string? issueId = "100", string? seriesId = null) => new()
    {
        Series = "Local Series (2020)", Chapters = "1", Volumes = "2020", Format = MangaFormat.Archive,
        ComicVineId = issueId, ComicVineSeriesId = seriesId
    };

    private const string SearchVolumeJson = """
        {"id":200,"name":"Example &amp; Friends","description":"<p>Series summary</p>",
         "start_year":"2020","publisher":{"name":"Example Publisher"},"count_of_issues":12,
         "image":{"small_url":"https://comicvine.gamespot.com/a/uploads/example.jpg"},
         "site_detail_url":"https://comicvine.gamespot.com/example/4050-200/","aliases":"Other title\nAnother title"}
        """;

    [Fact]
    public async Task SearchTitle_UsesVolumeResourceAndMapsResults()
    {
        var handler = new Handler(_ => Json("{\"status_code\":1,\"results\":[" + SearchVolumeJson + "]}"));
        using var service = Service(handler);
        var result = await service.SearchVolumesAsync("Example & Friends");
        Assert.Equal(MetadataProvider.ComicVine, result.Provider);
        var match = Assert.Single(result.Matches);
        Assert.Equal(200, match.Series.ComicVineId);
        Assert.Equal("Example & Friends", match.Series.Name);
        Assert.Equal("Series summary", match.Series.Summary);
        Assert.Equal(12, match.Series.Chapters);
        Assert.Equal(2020, match.Series.StartDate!.Value.Year);
        Assert.Equal(2, match.Series.Synonyms.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/search/", request.AbsolutePath);
        Assert.Contains("resources=volume", request.Query);
        Assert.Contains("query=Example%20%26%20Friends", request.Query);
    }

    [Theory]
    [InlineData("200")]
    [InlineData("comicvine:200")]
    [InlineData("https://comicvine.gamespot.com/example/4050-200/")]
    public async Task SearchIdOrUrl_UsesVerifiedVolumeLookup(string query)
    {
        var handler = new Handler(_ => Json("{\"status_code\":1,\"results\":" + SearchVolumeJson + "}"));
        using var service = Service(handler);
        var result = await service.SearchVolumesAsync(query);
        Assert.Equal(200, Assert.Single(result.Matches).Series.ComicVineId);
        Assert.Equal("/api/volume/4050-200/", Assert.Single(handler.Requests).AbsolutePath);
    }

    [Theory]
    [InlineData("https://comicvine.gamespot.com/example/4000-100/")]
    [InlineData("https://unrelated.example/4050-200/")]
    public async Task SearchInvalidUrl_DoesNotCallApi(string query)
    {
        var handler = new Handler();
        using var service = Service(handler);
        await Assert.ThrowsAsync<Kavita.Common.KavitaException>(() => service.SearchVolumesAsync(query));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchWithoutKey_ReturnsConfigurationHint()
    {
        var handler = new Handler();
        using var service = Service(handler, key: null);
        var error = await Assert.ThrowsAsync<Kavita.Common.KavitaException>(() => service.SearchVolumesAsync("Example"));
        Assert.Contains("Settings > Kavita+ > Comic Vine", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueId_FillsMetadataWithoutChangingGrouping()
    {
        var handler = new Handler();
        using var service = Service(handler);
        var info = Info();
        await service.EnrichAsync([info], LibraryType.ComicVine, true);
        var metadata = Assert.IsType<ComicInfo>(info.ComicInfo);
        Assert.Equal("The & Beginning", metadata.Title);
        Assert.Equal("Issue summary", metadata.Summary);
        Assert.Equal("Writer One", metadata.Writer);
        Assert.Equal("Writer One", metadata.Editor);
        Assert.Equal("Artist One", metadata.Penciller);
        Assert.Equal("Artist One", metadata.Inker);
        Assert.Equal("Artist One", metadata.CoverArtist);
        Assert.Equal("Colorist One", metadata.Colorist);
        Assert.Equal("Letterer One", metadata.Letterer);
        Assert.Equal("Example Publisher", metadata.Publisher);
        Assert.Equal("Hero", metadata.Characters);
        Assert.Equal("Heroes", metadata.Teams);
        Assert.Equal("Gotham", metadata.Locations);
        Assert.Equal("First Arc", metadata.StoryArc);
        Assert.Equal((2020, 5, 13), (metadata.Year, metadata.Month, metadata.Day));
        Assert.Contains("comicvine.gamespot.com/example/4000-100/", metadata.Web);
        Assert.Equal("200", info.ComicVineSeriesId);
        Assert.Equal("Local Series (2020)", info.Series);
        Assert.Equal("2020", info.Volumes);
        Assert.Equal("1", info.Chapters);
        Assert.Equal(0, metadata.Count); // An ongoing series' current issue count is not a completion marker.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EmbeddedValuesTakePriority()
    {
        using var service = Service(new Handler());
        var metadata = new ComicInfo { Title = "Local title", Summary = "Local summary", Writer = "Local writer",
            Year = 1999, Month = 2, Day = 3, Web = "https://example.org/local https://example.org/second" };
        var info = Info();
        info.ComicInfo = metadata;
        await service.EnrichAsync([info], LibraryType.ComicVine, true);
        Assert.Same(metadata, info.ComicInfo);
        Assert.Equal("Local title", metadata.Title);
        Assert.Equal("Local summary", metadata.Summary);
        Assert.Equal("Local writer", metadata.Writer);
        Assert.Equal((1999, 2, 3), (metadata.Year, metadata.Month, metadata.Day));
        Assert.Contains("https://example.org/local", metadata.Web);
        Assert.Contains("https://example.org/local,https://example.org/second,", metadata.Web);
    }

    [Theory]
    [InlineData(null, LibraryType.ComicVine, true)]
    [InlineData("", LibraryType.ComicVine, true)]
    [InlineData("test-key", LibraryType.ComicVine, false)]
    [InlineData("test-key", LibraryType.Manga, true)]
    [InlineData("test-key", LibraryType.Book, true)]
    public async Task Disabled_DoesNotContactApi(string? key, LibraryType type, bool enabled)
    {
        var handler = new Handler();
        using var service = Service(handler, key: key);
        var info = Info();
        await service.EnrichAsync([info], type, enabled);
        Assert.Empty(handler.Requests);
        Assert.Null(info.ComicInfo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("100/../../search")]
    public async Task MissingOrInvalidId_DoesNotContactApi(string? id)
    {
        var handler = new Handler();
        using var service = Service(handler);
        await service.EnrichAsync([Info(id)], LibraryType.ComicVine, true);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Placeholder_DoesNotContactApi()
    {
        var handler = new Handler();
        using var service = Service(handler);
        var info = Info();
        info.UnchangedFiles = ["unchanged.cbz"];
        await service.EnrichAsync([info], LibraryType.ComicVine, true);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SeriesId_ResolvesOneIssueByNumber()
    {
        var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/issues/")
            ? """{"status_code":1,"number_of_total_results":1,"results":[{"id":100,"issue_number":"1.5","volume":{"id":200}}]}"""
            : request.RequestUri.AbsolutePath.Contains("/volume/") ? VolumeJson : IssueJson));
        using var service = Service(handler);
        var info = Info(null, "200");
        info.Chapters = "001.50";
        await service.EnrichAsync([info], LibraryType.ComicVine, true);
        Assert.NotNull(info.ComicInfo);
        Assert.Equal("100", info.ComicVineId);
        Assert.Contains("volume:200,issue_number:1.5", Uri.UnescapeDataString(handler.Requests[0].Query));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1-2")]
    [InlineData("Annual")]
    public async Task UnknownOrRangeIssue_IsNotGuessed(string number)
    {
        var handler = new Handler();
        using var service = Service(handler);
        var info = Info(null, "200");
        info.Chapters = number;
        await service.EnrichAsync([info], LibraryType.ComicVine, true);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(2, "1", 200)]
    [InlineData(1, "2", 200)]
    [InlineData(1, "1", 999)]
    public async Task AmbiguousOrMismatchedList_IsNotApplied(int total, string number, int volume)
    {
        var json = JsonSerializer.Serialize(new { status_code = 1, number_of_total_results = total,
            results = new[] { new { id = 100, issue_number = number, volume = new { id = volume } } } });
        var handler = new Handler(_ => Json(json));
        using var service = Service(handler);
        var info = Info(null, "200");
        await service.EnrichAsync([info], LibraryType.ComicVine, true);
        Assert.Null(info.ComicInfo);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("999", null)]
    [InlineData("100", "999")]
    public async Task MismatchedDetail_IsNotApplied(string issueId, string? volumeId)
    {
        var handler = new Handler();
        using var service = Service(handler);
        var info = Info(issueId, volumeId);
        await service.EnrichAsync([info], LibraryType.ComicVine, true);
        Assert.Null(info.ComicInfo);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("{\"status_code\":100,\"error\":\"Invalid API Key\"}")]
    [InlineData("{\"status_code\":\"wrong type\"}")]
    public async Task ApiFailure_KeepsLocalMetadataAndPausesFurtherLookups(string json)
    {
        var handler = new Handler(_ => Json(json));
        using var service = Service(handler);
        var info = Info();
        info.ComicInfo = new ComicInfo { Summary = "Local" };
        await service.EnrichAsync([info, Info("101")], LibraryType.ComicVine, true);
        Assert.Equal("Local", info.ComicInfo.Summary);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HttpFailure_RespectsRetryAfter()
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        });
        var clock = new Clock();
        using var service = Service(handler, clock);
        await service.EnrichAsync([Info()], LibraryType.ComicVine, true);
        clock.Advance(TimeSpan.FromMinutes(30));
        await service.EnrichAsync([Info("101")], LibraryType.ComicVine, true);
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromMinutes(31));
        await service.EnrichAsync([Info("102")], LibraryType.ComicVine, true);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotFailImport()
    {
        var handler = new Handler(_ => throw new HttpRequestException("Request URI may contain a secret"));
        using var service = Service(handler);
        await service.EnrichAsync([Info(), Info("101")], LibraryType.ComicVine, true);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_IsPropagated()
    {
        using var service = Service(new Handler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnrichAsync([Info()], LibraryType.ComicVine, true, cts.Token));
    }

    [Fact]
    public async Task ConcurrentLookups_ShareCachedResponses()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { ResponseGate = gate };
        using var service = Service(handler);
        var first = Info();
        var second = Info();
        var firstLookup = service.EnrichAsync([first], LibraryType.ComicVine, true);
        var secondLookup = service.EnrichAsync([second], LibraryType.ComicVine, true);
        Assert.Single(handler.Requests);
        gate.SetResult();
        await Task.WhenAll(firstLookup, secondLookup);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(first.ComicInfo!.Summary, second.ComicInfo!.Summary);
        Assert.NotSame(first.ComicInfo, second.ComicInfo);
    }

    [Fact]
    public async Task CachedResponses_Expire()
    {
        var handler = new Handler();
        var clock = new Clock();
        using var service = Service(handler, clock);
        await service.EnrichAsync([Info(), Info()], LibraryType.ComicVine, true);
        Assert.Equal(2, handler.Requests.Count);
        clock.Advance(TimeSpan.FromDays(2));
        await service.EnrichAsync([Info()], LibraryType.ComicVine, true);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task RequestBudget_StopsWithoutBlockingScanForAnHour()
    {
        var handler = new Handler(request => Json(JsonSerializer.Serialize(new
        {
            status_code = 1,
            results = new { id = int.Parse(request.RequestUri!.Segments[^1].Trim('/').Split('-')[1]), name = "Issue" }
        })));
        var clock = new Clock();
        using var service = Service(handler, clock);
        var infos = Enumerable.Range(1, ComicVineService.RequestsPerHour + 1).Select(id => Info(id.ToString())).ToList();
        await service.EnrichAsync(infos, LibraryType.ComicVine, true);
        Assert.Equal(ComicVineService.RequestsPerHour, handler.Requests.Count);
        Assert.Null(infos[^1].ComicInfo);
        clock.Advance(TimeSpan.FromHours(1));
        await service.EnrichAsync([infos[^1]], LibraryType.ComicVine, true);
        Assert.NotNull(infos[^1].ComicInfo);
    }

    [Fact]
    public void Mapping_RejectsUntrustedAttributionUrlAndUsesCoverDateFallback()
    {
        using var issue = JsonDocument.Parse("""
            {"name":"Title","deck":"Fallback summary","store_date":null,"cover_date":"2021-04-01",
             "site_detail_url":"https://example.org/untrusted"}
            """);
        var metadata = new ComicInfo();
        ComicVineService.ApplyMissingMetadata(metadata, issue.RootElement, null);
        Assert.Equal("Fallback summary", metadata.Summary);
        Assert.Equal((2021, 4, 1), (metadata.Year, metadata.Month, metadata.Day));
        Assert.Empty(metadata.Web);
    }

    [Fact]
    public async Task FailureLogging_DoesNotExposeApiKeyOrRequestUri()
    {
        var handler = new Handler(_ => throw new HttpRequestException("https://comicvine.gamespot.com/api/?api_key=test-key"));
        var logger = new RecordingLogger();
        using var service = new ComicVineService(new HttpClient(handler), "test-key", logger, new Clock());
        await service.EnrichAsync([Info()], LibraryType.ComicVine, true);
        var message = Assert.Single(logger.Messages);
        Assert.DoesNotContain("test-key", message);
        Assert.DoesNotContain("api_key", message);
    }

    private sealed class RecordingLogger : ILogger<ComicVineService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    }

    [Fact]
    public async Task ReplacingKey_UsesNewCredentialAndClearsOldFailureCooldown()
    {
        string? key = "test-key-invalid";
        var handler = new Handler(request => request.RequestUri!.Query.Contains("test-key-invalid")
            ? Json("""{"status_code":100,"error":"Invalid key"}""") : Json(IssueJson));
        using var service = new ComicVineService(new HttpClient(handler), null,
            NullLogger<ComicVineService>.Instance, new Clock(), _ => Task.FromResult(key));
        Assert.False(await service.TestConnectionAsync());
        key = "test-key-replacement";
        Assert.True(await service.TestConnectionAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("api_key=test-key-replacement", handler.Requests[1].Query);
        key = null;
        Assert.False(await service.TestConnectionAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TestConnection_DoesNotReuseAStoredSuccessfulResponse()
    {
        var handler = new Handler();
        using var service = Service(handler);
        Assert.True(await service.TestConnectionAsync());
        handler.FailureStatus = HttpStatusCode.ServiceUnavailable;
        Assert.False(await service.TestConnectionAsync());
        Assert.Equal(2, handler.Requests.Count);
    }
}
