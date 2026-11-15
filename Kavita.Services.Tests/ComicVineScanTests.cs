using Kavita.Database.Tests;
using Kavita.Models.Metadata;
using Kavita.Services.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class ComicVineScanTests(ITestOutputHelper output) : AbstractDbTest(output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedMatch_ResolvesIssuesWithoutEmbeddedIds_AndHonorsDontMatch(bool dontMatch)
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        var helper = new ScannerHelper(unitOfWork, output);
        var library = await helper.GenerateScannerData("Matched API - ComicVine",
            ["Example (2020)/Example (2020) #001.cbz"],
            new Dictionary<string, ComicInfo>
            {
                ["Example (2020) #001.cbz"] = new() { Series = "Example", Volume = "2020", Number = "1" }
            });
        var firstScan = helper.CreateServices();
        await firstScan.ScanLibrary(library.Id, forceUpdate: true);
        var series = await context.Series.SingleAsync(s => s.LibraryId == library.Id);
        series.ComicVineId = "200";
        series.MetadataProviderOverride = Kavita.Models.Entities.Enums.MetadataProvider.ComicVine;
        series.DontMatch = dontMatch;
        await context.SaveChangesAsync();

        var handler = new ComicVineServiceTests.Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/issues/")
                ? ComicVineServiceTests.Json("""{"status_code":1,"number_of_total_results":1,"results":[{"id":100,"issue_number":"1","volume":{"id":200}}]}""")
                : ComicVineServiceTests.Json(request.RequestUri.AbsolutePath.Contains("/volume/")
                    ? """{"status_code":1,"results":{"id":200,"publisher":{"name":"Publisher"}}}"""
                    : """{"status_code":1,"results":{"id":100,"name":"Matched title","description":"Fetched summary","volume":{"id":200}}}"""));
        using var comicVine = ComicVineServiceTests.Service(handler);
        var scanner = helper.CreateServices(comicVineService: comicVine);
        await scanner.ScanSeries(series.Id, true);
        var chapter = await context.Chapter.SingleAsync();
        Assert.Equal("200", series.ComicVineId);
        if (dontMatch) Assert.Empty(handler.Requests);
        else
        {
            Assert.Equal("Matched title", chapter.TitleName);
            Assert.Equal("100", chapter.ComicVineId);
            Assert.Contains(handler.Requests, r => r.AbsolutePath == "/api/issues/");
        }
    }

    [Fact]
    public async Task Scan_StoresRemoteMetadataAndRespectsChapterLocksOnRescan()
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        var helper = new ScannerHelper(unitOfWork, output);
        var library = await helper.GenerateScannerData("Direct API - ComicVine",
            ["Example (2020)/Example (2020) #001.cbz"],
            new Dictionary<string, ComicInfo>
            {
                ["Example (2020) #001.cbz"] = new()
                {
                    Series = "Example", Volume = "2020", Number = "1",
                    Notes = "Scraped metadata from ComicVine [CVDB100]"
                }
            });
        var handler = new ComicVineServiceTests.Handler();
        var clock = new ComicVineServiceTests.Clock();
        using var comicVine = ComicVineServiceTests.Service(handler, clock);
        var scanner = helper.CreateServices(comicVineService: comicVine);
        await scanner.ScanLibrary(library.Id, forceUpdate: true);

        var series = await context.Series.Include(s => s.Metadata).Include(s => s.Volumes)
            .ThenInclude(v => v.Chapters).SingleAsync(s => s.LibraryId == library.Id);
        var chapter = Assert.Single(Assert.Single(series.Volumes).Chapters);
        Assert.Equal("The & Beginning", chapter.TitleName);
        Assert.Equal("Issue summary", chapter.Summary);
        Assert.Equal("Issue summary", series.Metadata.Summary);
        Assert.Equal("200", series.ComicVineId);

        // An outage after the in-memory cache expires must not erase fetched, unlocked fields.
        clock.Advance(TimeSpan.FromDays(2));
        handler.FailureStatus = System.Net.HttpStatusCode.ServiceUnavailable;
        await scanner.ScanLibrary(library.Id, forceUpdate: true);
        await context.Entry(chapter).ReloadAsync();
        Assert.Equal("The & Beginning", chapter.TitleName);
        Assert.Equal("Issue summary", chapter.Summary);
        Assert.Contains("comicvine.gamespot.com", chapter.WebLinks);
        handler.FailureStatus = null;
        clock.Advance(TimeSpan.FromMinutes(6));

        chapter.TitleName = "My locked title";
        chapter.TitleNameLocked = true;
        chapter.Summary = "My locked summary";
        chapter.SummaryLocked = true;
        series.Metadata.Summary = "My locked series summary";
        series.Metadata.SummaryLocked = true;
        await context.SaveChangesAsync();

        await scanner.ScanLibrary(library.Id, forceUpdate: true);
        await context.Entry(chapter).ReloadAsync();
        await context.Entry(series.Metadata).ReloadAsync();
        Assert.Equal("My locked title", chapter.TitleName);
        Assert.Equal("My locked summary", chapter.Summary);
        Assert.Equal("My locked series summary", series.Metadata.Summary);
        Assert.Equal(5, handler.Requests.Count); // Initial issue/volume, outage, then refreshed issue/volume.
    }
}
