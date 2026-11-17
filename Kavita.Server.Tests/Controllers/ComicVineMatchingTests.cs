using System.Net.Http;
using System.Reflection;
using Kavita.API.Services.Metadata;
using Kavita.API.Services;
using Kavita.API.Services.Plus;
using Kavita.API.Store;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Server.Attributes;
using Kavita.Server.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Kavita.Server.Tests.Controllers;

public class ComicVineMatchingTests
{
    [Fact]
    public void DirectMatching_RequiresAdminAndKavitaPlusLicense()
    {
        var controller = typeof(ComicVineController);
        Assert.Contains(controller.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PolicyGroups.AdminPolicy);
        Assert.NotNull(controller.GetCustomAttribute<KPlusAttribute>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderEndpoints_EnforceActiveLicenseBeforeCallingActions(bool active)
    {
        var user = Substitute.For<IUserContext>();
        user.IsAuthenticated.Returns(true);
        var license = Substitute.For<ILicenseService>();
        license.HasActiveLicense(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(active);
        using var services = new ServiceCollection()
            .AddSingleton(user).AddSingleton(license)
            .AddSingleton(Substitute.For<ILocalizationService>()).BuildServiceProvider();
        var settingsEndpoints = new[]
        {
            nameof(SettingsController.GetComicVineSettings), nameof(SettingsController.SaveComicVineSettings),
            nameof(SettingsController.RemoveComicVineSettings), nameof(SettingsController.TestComicVineConnection)
        }.Select(name => typeof(SettingsController).GetMethod(name)!);
        var matchingEndpoints = typeof(ComicVineController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var endpoint in settingsEndpoints.Concat(matchingEndpoints))
        {
            var guard = endpoint.GetCustomAttribute<KPlusAttribute>()
                ?? endpoint.DeclaringType!.GetCustomAttribute<KPlusAttribute>();
            Assert.NotNull(guard);
            var http = new DefaultHttpContext { RequestServices = services };
            var context = new AuthorizationFilterContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
            await guard.OnAuthorizationAsync(context);
            if (active) Assert.Null(context.Result);
            else Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(context.Result).StatusCode);
        }
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
