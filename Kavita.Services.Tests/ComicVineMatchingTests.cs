using Kavita.API.Services.Metadata;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.KavitaPlus.Manage;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Entities.Person;
using Kavita.Services.Metadata;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class ComicVineMatchingTests(ITestOutputHelper output) : AbstractDbTest(output)
{
    [Theory]
    [InlineData(LibraryType.Comic)]
    [InlineData(LibraryType.ComicVine)]
    public async Task Apply_PersistsVerifiedVolumeAndProvider_WithoutKavitaPlus(LibraryType type)
    {
        var (uow, db, mapper) = await CreateDatabase();
        var library = new LibraryBuilder("Comics", type).WithMetadataProvider(MetadataProvider.ComicBookRoundup).Build();
        db.Library.Add(library);
        await db.SaveChangesAsync();
        var series = new SeriesBuilder("Example").WithLibraryId(library.Id)
            .WithMetadata(new SeriesMetadataBuilder().Build()).Build();
        series.DontMatch = true;
        series.IsBlacklisted = true;
        db.Series.Add(series);
        await db.SaveChangesAsync();
        var api = Substitute.For<IComicVineService>();
        api.GetVolumeAsync(200, Arg.Any<CancellationToken>()).Returns(new ExternalSeriesDetailDto
        {
            ComicVineId = 200, Summary = "Selected volume synopsis", StartDate = new DateTime(1986, 1, 1),
            Publisher = "Selected publisher", SiteUrl = "https://comicvine.gamespot.com/example/4050-200/"
        });
        var service = new ComicVineMatchingService(uow, api, mapper, Substitute.For<IEventHub>(),
            Substitute.For<Kavita.API.Services.Plus.IKavitaPlusAuditService>(), Substitute.For<Kavita.API.Store.IUserContext>());

        await service.ApplyAsync(series.Id, 200);
        await db.Entry(series).ReloadAsync();
        Assert.Equal("200", series.ComicVineId);
        Assert.Equal(MetadataProvider.ComicVine, series.MetadataProviderOverride);
        Assert.False(series.DontMatch);
        Assert.False(series.IsBlacklisted);
        // Read persisted data, rather than accepting only in-memory changes or a queued scan.
        db.ChangeTracker.Clear();
        var saved = await db.Series.Include(x => x.Metadata).ThenInclude(x => x.People).ThenInclude(x => x.Person)
            .SingleAsync(x => x.Id == series.Id);
        Assert.Equal("Selected volume synopsis", saved.Metadata.Summary);
        Assert.Equal(1986, saved.Metadata.ReleaseYear);
        Assert.Equal("Selected publisher", Assert.Single(saved.Metadata.People).Person.Name);
        Assert.Contains("4050-200", saved.Metadata.WebLinks);
        Assert.False(saved.Metadata.SummaryLocked);
        Assert.Equal("Example", saved.Name); // Matching must not change file grouping.

        api.SearchVolumesAsync("comicvine:200", Arg.Any<CancellationToken>()).Returns(new MatchSeriesResultDto());
        await service.SearchAsync(series.Id, "");
        await api.Received(1).SearchVolumesAsync("comicvine:200", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_RespectsUserLocks_AndAllowsRematchingUnlockedFields()
    {
        var (uow, db, mapper) = await CreateDatabase();
        var library = new LibraryBuilder("Comics", LibraryType.ComicVine).Build();
        db.Library.Add(library);
        await db.SaveChangesAsync();
        var series = new SeriesBuilder("Local title").WithLibraryId(library.Id)
            .WithMetadata(new SeriesMetadataBuilder().Build()).Build();
        series.Metadata.Summary = "My synopsis";
        series.Metadata.SummaryLocked = true;
        series.Metadata.ReleaseYear = 1999;
        series.Metadata.ReleaseYearLocked = true;
        series.Metadata.PublisherLocked = true;
        series.Metadata.WebLinks = "https://example.org/local,https://comicvine.gamespot.com/old/4050-99/";
        series.Metadata.People.Add(new SeriesMetadataPeople
        {
            Person = new PersonBuilder("My publisher").Build(), Role = PersonRole.Publisher
        });
        db.Series.Add(series);
        await db.SaveChangesAsync();
        var api = Substitute.For<IComicVineService>();
        api.GetVolumeAsync(200, Arg.Any<CancellationToken>()).Returns(new ExternalSeriesDetailDto
        {
            ComicVineId = 200, Summary = "Remote synopsis", StartDate = new DateTime(2001, 1, 1),
            Publisher = "Remote publisher", SiteUrl = "https://comicvine.gamespot.com/new/4050-200/"
        });
        var service = new ComicVineMatchingService(uow, api, mapper, Substitute.For<IEventHub>(),
            Substitute.For<Kavita.API.Services.Plus.IKavitaPlusAuditService>(), Substitute.For<Kavita.API.Store.IUserContext>());
        await service.ApplyAsync(series.Id, 200);
        Assert.Equal("My synopsis", series.Metadata.Summary);
        Assert.Equal(1999, series.Metadata.ReleaseYear);
        Assert.Equal("My publisher", Assert.Single(series.Metadata.People).Person.Name);
        Assert.Contains("https://example.org/local", series.Metadata.WebLinks);
        Assert.DoesNotContain("4050-99", series.Metadata.WebLinks);
        Assert.Contains("4050-200", series.Metadata.WebLinks);

        series.Metadata.SummaryLocked = false;
        series.Metadata.ReleaseYearLocked = false;
        series.Metadata.PublisherLocked = false;
        await db.SaveChangesAsync();
        await service.ApplyAsync(series.Id, 200);
        Assert.Equal("Remote synopsis", series.Metadata.Summary);
        Assert.Equal(2001, series.Metadata.ReleaseYear);
        Assert.Equal("Remote publisher", Assert.Single(series.Metadata.People).Person.Name);
        await service.ApplyAsync(series.Id, 200);
        Assert.Single(series.Metadata.People);
        Assert.Equal(1, await db.Person.CountAsync(p => p.Name == "Remote publisher"));
    }

    [Fact]
    public async Task Apply_RejectsMismatchedResponseAndUnsupportedLibrary()
    {
        var (uow, db, mapper) = await CreateDatabase();
        var series = new SeriesBuilder("Manga").WithLibraryId(1).Build();
        db.Series.Add(series);
        await db.SaveChangesAsync();
        var api = Substitute.For<IComicVineService>();
        var service = new ComicVineMatchingService(uow, api, mapper, Substitute.For<IEventHub>(),
            Substitute.For<Kavita.API.Services.Plus.IKavitaPlusAuditService>(), Substitute.For<Kavita.API.Store.IUserContext>());
        await Assert.ThrowsAsync<KavitaException>(() => service.ApplyAsync(series.Id, 200));
        await api.DidNotReceive().GetVolumeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        var library = await db.Library.SingleAsync();
        library.Type = LibraryType.ComicVine;
        await db.SaveChangesAsync();
        api.GetVolumeAsync(200, Arg.Any<CancellationToken>()).Returns(new ExternalSeriesDetailDto { ComicVineId = 999 });
        await Assert.ThrowsAsync<KavitaException>(() => service.ApplyAsync(series.Id, 200));
        Assert.True(string.IsNullOrEmpty(series.ComicVineId));
    }

    [Fact]
    public async Task CountsAndPagedFilters_UseComicVineIdsInsteadOfPaidMatchState()
    {
        var (uow, db, mapper) = await CreateDatabase();
        var library = new LibraryBuilder("Comics", LibraryType.ComicVine).Build();
        db.Library.Add(library);
        await db.SaveChangesAsync();
        foreach (var (name, id, dontMatch) in new[] { ("Matched", "200", false), ("Missing", "", false), ("Ignored", "", true) })
        {
            var series = new SeriesBuilder(name).WithLibraryId(library.Id)
                .WithMetadata(new SeriesMetadataBuilder().Build()).Build();
            series.ComicVineId = id;
            series.DontMatch = dontMatch;
            series.IsBlacklisted = true; // Paid blacklist must not affect direct matching.
            db.Series.Add(series);
        }
        db.Series.Add(new SeriesBuilder("Other library").WithLibraryId(1).Build());
        await db.SaveChangesAsync();
        var service = new ComicVineMatchingService(uow, Substitute.For<IComicVineService>(), mapper, Substitute.For<IEventHub>(),
            Substitute.For<Kavita.API.Services.Plus.IKavitaPlusAuditService>(), Substitute.For<Kavita.API.Store.IUserContext>());
        var counts = await service.CountsAsync();
        Assert.Equal(3, counts.TotalCount);
        Assert.Equal(1, counts.NotMatchedCount);
        Assert.Equal(1, counts.DontMatchCount);
        Assert.Equal(0, counts.ErroredCount);
        var rows = await service.ListAsync(new ManageMatchFilterDto { LibraryType = -1, MatchStateOption = MatchStateOption.Matched }, new UserParams());
        Assert.Equal(1, rows.TotalCount);
        Assert.True(Assert.Single(rows).IsMatched);
        Assert.False(rows[0].Series.IsBlacklisted);
        var unmatched = await service.ListAsync(new ManageMatchFilterDto { LibraryType = -1, MatchStateOption = MatchStateOption.NotMatched }, new UserParams());
        Assert.Equal("Missing", Assert.Single(unmatched).Series.Name);
        await service.SetDontMatchAsync(unmatched[0].Series.Id, true);
        Assert.Equal(2, (await service.CountsAsync()).DontMatchCount);
    }
}
