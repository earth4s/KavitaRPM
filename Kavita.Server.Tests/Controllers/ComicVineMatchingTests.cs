using System.Net.Http;
using System.Reflection;
using Kavita.API.Services.Metadata;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Server.Attributes;
using Kavita.Server.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Kavita.Server.Tests.Controllers;

public class ComicVineMatchingTests
{
    [Fact]
    public void DirectMatching_IsAdminOnlyWithoutKavitaPlusLicense()
    {
        var controller = typeof(ComicVineController);
        Assert.Contains(controller.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PolicyGroups.AdminPolicy);
        Assert.Null(controller.GetCustomAttribute<KPlusAttribute>());
        foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.Null(method.GetCustomAttribute<KPlusAttribute>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiFailures_NeverReturnSecretRequestUrls(bool saving)
    {
        var matching = Substitute.For<IComicVineMatchingService>();
        var error = new HttpRequestException("GET https://comicvine.gamespot.com/api/?api_key=secret-key failed");
        matching.SearchAsync(1, "Example", Arg.Any<CancellationToken>()).ThrowsAsync(error);
        matching.ApplyAsync(1, 200, Arg.Any<CancellationToken>()).ThrowsAsync(error);
        var controller = new ComicVineController(matching)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = saving
            ? await controller.Apply(1, new ComicVineMatchDto { VolumeId = 200 })
            : (await controller.Search(1, new ComicVineSearchDto { Query = "Example" })).Result;
        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, response.StatusCode);
        Assert.DoesNotContain("secret-key", response.Value!.ToString());
        Assert.DoesNotContain("api_key", response.Value!.ToString());
    }
}
