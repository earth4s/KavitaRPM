using Kavita.API.Services.Metadata;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.KavitaPlus.Manage;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.Entities.Enums;
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
        api.GetVolumeAsync(200, Arg.Any<CancellationToken>()).Returns(new ExternalSeriesDetailDto { ComicVineId = 200 });
        var service = new ComicVineMatchingService(uow, api, mapper);

        await service.ApplyAsync(series.Id, 200);
        await db.Entry(series).ReloadAsync();
        Assert.Equal("200", series.ComicVineId);
        Assert.Equal(MetadataProvider.ComicVine, series.MetadataProviderOverride);
        Assert.False(series.DontMatch);
        Assert.False(series.IsBlacklisted);

        api.SearchVolumesAsync("comicvine:200", Arg.Any<CancellationToken>()).Returns(new MatchSeriesResultDto());
        await service.SearchAsync(series.Id, "");
        await api.Received(1).SearchVolumesAsync("comicvine:200", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_RejectsMismatchedResponseAndUnsupportedLibrary()
    {
        var (uow, db, mapper) = await CreateDatabase();
        var series = new SeriesBuilder("Manga").WithLibraryId(1).Build();
        db.Series.Add(series);
        await db.SaveChangesAsync();
        var api = Substitute.For<IComicVineService>();
        var service = new ComicVineMatchingService(uow, api, mapper);
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
        var service = new ComicVineMatchingService(uow, Substitute.For<IComicVineService>(), mapper);
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
