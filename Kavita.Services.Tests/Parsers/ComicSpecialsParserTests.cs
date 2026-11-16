using System.IO.Abstractions.TestingHelpers;
using Kavita.Database.Tests;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Metadata;
using Kavita.Services.Scanner;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kavita.Services.Tests.Parsers;

public class ComicSpecialsParserTests : AbstractFsTest
{
    [Theory]
    [InlineData(LibraryType.Comic, "Specials", "Annual 001.cbz")]
    [InlineData(LibraryType.Comic, "Specials", "Annual 001.png")]
    [InlineData(LibraryType.Comic, "specials", "Annual 001.cbz")]
    [InlineData(LibraryType.Comic, "SPECIALS/Extras", "Bonus sp01.cbz")]
    [InlineData(LibraryType.ComicVine, "Specials", "Annual 001.cbz")]
    [InlineData(LibraryType.ComicVine, "Specials", "Annual 001.png")]
    [InlineData(LibraryType.ComicVine, "specials", "Annual 001.cbz")]
    [InlineData(LibraryType.ComicVine, "SPECIALS/Extras", "Bonus sp01.cbz")]
    public void SpecialsFolder_UsesParentComicAndSpecialVolume(LibraryType type, string folder, string filename)
    {
        var fs = new MockFileSystem();
        var root = Root + "Comics/";
        var owner = root + "The New Mutants/";
        var path = owner + folder + "/" + filename;
        fs.AddFile(path, new MockFileData(""));
        var ds = new DirectoryService(NullLogger<DirectoryService>.Instance, fs);
        IDefaultParser parser = type == LibraryType.ComicVine ? new ComicVineParser(ds) : new BasicParser(ds, new ImageParser(ds));
        foreach (var scanRoot in new[] { root, owner, owner + folder + "/" })
        {
            var result = parser.Parse(path, scanRoot, root, type);
            Assert.True(result.Success);
            Assert.Equal("The New Mutants", result.Info!.Series);
            Assert.Equal(Parser.SpecialVolume, result.Info.Volumes);
            Assert.True(result.Info.IsSpecial);
        }
    }

    [Theory]
    [InlineData(LibraryType.Comic)]
    [InlineData(LibraryType.ComicVine)]
    public void OrdinarySeriesEndingInSpecials_IsNotAContainer(LibraryType type)
    {
        var fs = new MockFileSystem();
        var root = Root + "Comics/";
        var owner = root + "Holiday Specials/";
        var path = owner + "Holiday Specials 001.cbz";
        fs.AddFile(path, new MockFileData(""));
        var ds = new DirectoryService(NullLogger<DirectoryService>.Instance, fs);
        IDefaultParser parser = type == LibraryType.ComicVine ? new ComicVineParser(ds) : new BasicParser(ds, new ImageParser(ds));
        var result = parser.Parse(path, owner, root, type);
        Assert.Equal("Holiday Specials", result.Info!.Series);
        Assert.False(result.Info.IsSpecial);
    }
    [Theory]
    [InlineData("Specials")]
    [InlineData("Holiday Specials")]
    public void LibraryBoundary_DoesNotInventAParentComic(string name)
    {
        var root = Root + "Comics/";
        Assert.Null(Parser.GetSpecialsOwnerFolder(root + name + "/Annual 001.cbz", root));
        Assert.Null(Parser.GetSpecialsOwnerFolder(Root + "Outside/Batman/Specials/Annual.cbz", root));
        Assert.Equal(Parser.NormalizePath(Root + "Batman"),
            Parser.NormalizePath(Parser.GetSpecialsOwnerFolder(Root + "Batman/Specials/Annual.cbz", Root)));
    }

}
