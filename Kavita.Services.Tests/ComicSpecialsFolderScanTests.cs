using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Services.Metadata;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Services.Scanner;
using Kavita.Services.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class ComicSpecialsFolderScanTests(ITestOutputHelper output) : AbstractDbTest(output)
{
    [Theory]
    [InlineData(LibraryType.Comic, false, "Specials")]
    [InlineData(LibraryType.Comic, false, "specials")]
    [InlineData(LibraryType.Comic, true, "Specials/Extras")]
    [InlineData(LibraryType.Comic, true, "Specials")]
    [InlineData(LibraryType.ComicVine, false, "Specials")]
    [InlineData(LibraryType.ComicVine, false, "SPECIALS")]
    [InlineData(LibraryType.ComicVine, true, "Specials/Extras")]
    [InlineData(LibraryType.ComicVine, true, "Specials")]
    public async Task SpecialsBelongToEachParent_AcrossFullAndIncrementalScans(LibraryType type, bool metadata, string specialsFolder)
    {
        var (uow, db, _) = await CreateDatabase();
        var helper = new ScannerHelper(uow, output);
        var files = new List<string>
        {
            "The New Mutants/The New Mutants 001.cbz",
            $"The New Mutants/{specialsFolder}/The New Mutants Annual 001.cbz",
            "Batman/Batman 001.cbz",
            $"Batman/{specialsFolder}/Batman Annual 001.cbz"
        };
        Dictionary<string, ComicInfo>? info = metadata ? new()
        {
            ["The New Mutants 001.cbz"] = new() { Series = "The New Mutants", Volume = "1983", Number = "1" },
            ["The New Mutants Annual 001.cbz"] = new() { Series = "The New Mutants Annual", Volume = "2000", Number = "1", Format = "Annual" },
            ["Batman 001.cbz"] = new() { Series = "Batman", Volume = "1940", Number = "1" },
            ["Batman Annual 001.cbz"] = new() { Series = "Batman Annual", Volume = "2000", Number = "1", Format = "Annual" }
        } : null;
        var library = await helper.GenerateScannerData($"Specials Ownership - {type}", files, info);
        var scanner = helper.CreateServices();
        await scanner.ScanLibrary(library.Id, forceUpdate: true);
        var parentName = metadata && type == LibraryType.ComicVine ? "The New Mutants (1983)" : "The New Mutants";
        var series = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .Where(s => s.LibraryId == library.Id).ToListAsync();
        Assert.Equal(2, series.Count);
        var parent = Assert.Single(series.Where(s => s.Name == parentName));
        Assert.Equal(2, parent.Volumes.Sum(v => v.Chapters.Count));
        Assert.Single(Assert.Single(parent.Volumes.Where(v => v.MinNumber == Parser.SpecialVolumeNumber)).Chapters);
        Assert.DoesNotContain(series, s => s.Name == "Specials");

        var added = Path.Join(library.Folders.First().Path, $"The New Mutants/{specialsFolder}/The New Mutants Annual 002.cbz");
        await helper.Scaffold(library.Folders.First().Path, [$"The New Mutants/{specialsFolder}/The New Mutants Annual 002.cbz"]);
        File.SetLastWriteTime(added, DateTime.Now.AddSeconds(2));
        await scanner.ScanLibrary(library.Id);
        db.ChangeTracker.Clear();
        var incremental = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .Where(s => s.LibraryId == library.Id).ToListAsync();
        Assert.Equal(2, incremental.Count);
        Assert.Equal(3, Assert.Single(incremental, s => s.Id == parent.Id).Volumes.Sum(v => v.Chapters.Count));
        Assert.Equal(2, Assert.Single(incremental, s => s.Id != parent.Id).Volumes.Sum(v => v.Chapters.Count));
        await scanner.ScanSeries(parent.Id, true);
        db.ChangeTracker.Clear();
        var after = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .Where(s => s.LibraryId == library.Id).ToListAsync();
        Assert.Equal(2, after.Count);
        var saved = Assert.Single(after.Where(s => s.Id == parent.Id));
        Assert.Equal(3, saved.Volumes.Sum(v => v.Chapters.Count));
        Assert.Equal(2, Assert.Single(saved.Volumes.Where(v => v.MinNumber == Parser.SpecialVolumeNumber)).Chapters.Count);
    }
    [Theory]
    [InlineData(LibraryType.Comic)]
    [InlineData(LibraryType.ComicVine)]
    public async Task ForcedScan_RepairsPreviouslySplitSpecialsSeries(LibraryType type)
    {
        var (uow, db, _) = await CreateDatabase();
        var helper = new ScannerHelper(uow, output);
        var library = await helper.GenerateScannerData($"Repair Specials - {type}",
            ["Batman/Batman 001.cbz", "Batman/Specials/Batman Annual 001.cbz"]);
        var scanner = helper.CreateServices();
        await scanner.ScanLibrary(library.Id, forceUpdate: true);
        var parent = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .SingleAsync(s => s.LibraryId == library.Id);
        // Reproduce the old database layout, with the annual's volume in a bogus series.
        var special = Assert.Single(parent.Volumes, v => v.MinNumber == Parser.SpecialVolumeNumber);
        var broken = new SeriesBuilder("Specials").WithLibraryId(library.Id)
            .WithMetadata(new SeriesMetadataBuilder().Build()).Build();
        broken.Format = parent.Format;
        broken.FolderPath = Path.Join(library.Folders.First().Path, "Batman/Specials");
        broken.LowestFolderPath = broken.FolderPath;
        parent.Volumes.Remove(special);
        broken.Volumes.Add(special);
        special.Series = broken;
        db.Series.Add(broken);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Series.CountAsync(s => s.LibraryId == library.Id));

        await scanner.ScanLibrary(library.Id, forceUpdate: true);
        db.ChangeTracker.Clear();
        var repaired = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .SingleAsync(s => s.LibraryId == library.Id);
        Assert.Equal(parent.Id, repaired.Id);
        Assert.Equal("Batman", repaired.Name);
        Assert.Equal(2, repaired.Volumes.Sum(v => v.Chapters.Count));
        Assert.Single(Assert.Single(repaired.Volumes, v => v.MinNumber == Parser.SpecialVolumeNumber).Chapters);
    }

    [Fact]
    public async Task ParentComicVineMatch_DoesNotReplaceAnAnnualsIssueId()
    {
        var (uow, db, _) = await CreateDatabase();
        var helper = new ScannerHelper(uow, output);
        var library = await helper.GenerateScannerData("Annual API identity - ComicVine",
            ["Batman (2000)/Batman 001.cbz", "Batman (2000)/Specials/Batman Annual 001.cbz"],
            new Dictionary<string, ComicInfo>
            {
                ["Batman 001.cbz"] = new() { Series = "Batman", Volume = "2000", Number = "1", Notes = "ComicVine [CVDB100]" },
                ["Batman Annual 001.cbz"] = new() { Series = "Batman Annual", Volume = "2001", Number = "1", Notes = "ComicVine [CVDB101]" }
            });
        var handler = new ComicVineServiceTests.Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/issues/")) return ComicVineServiceTests.Json("""{"status_code":1,"number_of_total_results":1,"results":[{"id":100,"issue_number":"1","volume":{"id":200}}]}""");
            if (path.Contains("/volume/")) return ComicVineServiceTests.Json(path.Contains("4050-201")
                ? """{"status_code":1,"results":{"id":201}}"""
                : """{"status_code":1,"results":{"id":200}}""");
            return ComicVineServiceTests.Json(path.Contains("4000-101")
                ? """{"status_code":1,"results":{"id":101,"name":"Annual story","volume":{"id":201}}}"""
                : """{"status_code":1,"results":{"id":100,"name":"Regular story","volume":{"id":200}}}""");
        });
        using var api = ComicVineServiceTests.Service(handler);
        var scanner = helper.CreateServices(comicVineService: api);
        await scanner.ScanLibrary(library.Id, forceUpdate: true);
        var parent = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .SingleAsync(s => s.LibraryId == library.Id);
        Assert.Equal("200", parent.ComicVineId);
        parent.MetadataProviderOverride = MetadataProvider.ComicVine;
        await db.SaveChangesAsync();
        await scanner.ScanSeries(parent.Id, true);
        db.ChangeTracker.Clear();
        var saved = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .SingleAsync(s => s.LibraryId == library.Id);
        var annual = Assert.Single(Assert.Single(saved.Volumes, v => v.MinNumber == Parser.SpecialVolumeNumber).Chapters);
        Assert.Equal("101", annual.ComicVineId);
        Assert.Equal("Annual story", annual.TitleName);
        Assert.Equal("200", saved.ComicVineId);
    }

    [Theory]
    [InlineData(LibraryType.Comic)]
    [InlineData(LibraryType.ComicVine)]
    public async Task TopLevelSeriesNamedSpecials_IsSeparateFromNestedContainers(LibraryType type)
    {
        var (uow, db, _) = await CreateDatabase();
        var helper = new ScannerHelper(uow, output);
        var library = await helper.GenerateScannerData($"Actual Specials Title - {type}",
            ["Specials/Specials 001.cbz", "Holiday Specials/Holiday Specials 001.cbz",
             "Batman/Batman 001.cbz", "Batman/Specials/Batman Annual 001.cbz"]);
        await helper.CreateServices().ScanLibrary(library.Id, forceUpdate: true);
        var rows = await db.Series.Include(s => s.Volumes).ThenInclude(v => v.Chapters)
            .Where(s => s.LibraryId == library.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, s => s.Name == "Specials");
        Assert.Single(rows, s => s.Name == "Holiday Specials");
        Assert.Equal(2, Assert.Single(rows, s => s.Name == "Batman").Volumes.Sum(v => v.Chapters.Count));
    }

}
