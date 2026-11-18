using System.Net;
using System.Text.Json;
using Kavita.API.Services.Plus;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Database.Tests;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Models.Parser;
using Kavita.Services.Metadata;
using Microsoft.AspNetCore.DataProtection;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class GcdServiceTests(ITestOutputHelper output) : AbstractDbTest(output)
{
    private const string Series = """{"api_url":"https://www.comics.org/api/series/6139/","name":"Example","year_began":1999}""";
    private const string Issue = """
        {"api_url":"https://www.comics.org/api/issue/256114/","series":"https://www.comics.org/api/series/6139/",
         "number":"12","title":"","key_date":"2000-11-00","on_sale_date":"2000-09-20","story_set":[
         {"type":"cover","colors":"Cover colorist","pencils":"Cover artist"},
         {"type":"comic story","title":"The story","script":"Writer","colors":"Interior colorist","inks":"None","synopsis":"Synopsis"},
         {"type":"illustration","script":"Other"}]}
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            Assert.Equal("www.comics.org", request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            return Task.FromResult(respond(request));
        }
    }

    [Theory]
    [InlineData("gcd:series:6139", false, 6139)]
    [InlineData("https://www.comics.org/api/issue/256114/", true, 256114)]
    public void ParsesTypedIds(string value, bool issue, int id) =>
        Assert.Equal(new GcdReference(issue, id), GcdReference.TryParse(value));

    [Theory]
    [InlineData("https://www.comics.org.example.org/api/issue/12/")]
    [InlineData("http://www.comics.org/issue/12/")]
    [InlineData("gcd:series:0")]
    [InlineData("comicvine:6139")]
    public void RejectsUnrelatedIdentities(string value) => Assert.Null(GcdReference.TryParse(value));

    [Fact]
    public async Task RevocationBlocksSettingsCacheAndBackgroundRequests()
    {
        var (uow, _, _) = await CreateDatabase();
        var license = Substitute.For<ILicenseService>();
        license.HasActiveLicense(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);
        var settings = new GcdSettingsService(uow, new EphemeralDataProtectionProvider(), license);
        await settings.SaveAsync(new GcdCredentialsDto("reader", "p@ss"), default);
        var stored = await uow.SettingsRepository.GetSettingAsync(ServerSettingKey.GcdCredentials);
        Assert.DoesNotContain("p@ss", stored.Value);
        var handler = new Handler(_ => ComicVineServiceTests.Json(Series));
        using var service = new GcdService(new HttpClient(handler), settings.GetAuthorizationAsync, ":memory:", TimeProvider.System);
        Assert.Equal(6139, (await service.GetVolumeAsync(6139))?.GcdId);
        license.HasActiveLicense(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<KavitaException>(() => service.GetVolumeAsync(6139));
        await Assert.ThrowsAsync<KavitaException>(() => settings.GetStatusAsync(default));
        await Assert.ThrowsAsync<KavitaException>(() => settings.RemoveAsync(default));
        await Assert.ThrowsAsync<KavitaException>(() => settings.SaveAsync(new("other","other"), default));
        Assert.False(await service.EnrichAsync([], 6139));
        Assert.False(await service.TestConnectionAsync());
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueListUsesVerifiedParentAndRejectsVariants(bool variant)
    {
        var item = """{"api_url":"https://www.comics.org/api/issue/256114/","series":"https://www.comics.org/api/series/6139/"}""";
        var list = variant ? "{\"count\":2,\"results\":[" + item + "," + item + "]}" : "{\"count\":1,\"results\":[" + item + "]}";
        var handler = new Handler(r => ComicVineServiceTests.Json(r.RequestUri!.AbsolutePath switch
        {
            "/api/series/6139/" => Series,
            "/api/series/name/Example/issue/12/" => list,
            "/api/issue/256114/" => Issue,
            _ => throw new InvalidOperationException()
        }));
        using var service = new GcdService(new HttpClient(handler), _ => Task.FromResult<string?>("test"), ":memory:", TimeProvider.System);
        var info = new ParserInfo { Series = "Example", Chapters = "12", ComicInfo = new ComicInfo { Writer = "Embedded" } };
        Assert.True(await service.EnrichAsync([info], 6139));
        if (variant) { Assert.Null(info.GcdId); return; }
        Assert.Equal(256114, info.GcdId);
        Assert.Equal("The story", info.ComicInfo.Title);
        Assert.Equal("Cover artist", info.ComicInfo.CoverArtist);
        Assert.Equal("Interior colorist", info.ComicInfo.Colorist);
        Assert.Equal("Embedded", info.ComicInfo.Writer);
        Assert.Empty(info.ComicInfo.Inker);
        Assert.Equal((2000, 9, 20), (info.ComicInfo.Year, info.ComicInfo.Month, info.ComicInfo.Day));
        Assert.Empty(await service.GetCoversAsync(256114, true));
    }

    [Fact]
    public async Task CooldownSurvivesRestartAndHealthDoesNotSpendRequests()
    {
        var path = Path.GetTempFileName();
        try
        {
            var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            using (var first = new GcdService(new HttpClient(handler), _ => Task.FromResult<string?>("test"), path, TimeProvider.System))
                Assert.False(await first.TestConnectionAsync());
            var next = new Handler(_ => throw new InvalidOperationException());
            using var second = new GcdService(new HttpClient(next), _ => Task.FromResult<string?>("test"), path, TimeProvider.System);
            var health = await second.GetHealthAsync();
            Assert.Equal(1, health.RequestsLastDay);
            Assert.Equal(1, health.RequestsLastMinute);
            Assert.NotNull(health.RetryAt);
            Assert.False(await second.TestConnectionAsync());
            Assert.Equal(0, next.Count);
        }
        finally { File.Delete(path); }
    }
}
