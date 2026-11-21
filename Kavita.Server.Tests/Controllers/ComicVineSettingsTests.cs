using System.Reflection;
using System.Text.Json;
using Kavita.API.Services.Metadata;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Settings;
using Kavita.Server.Controllers;
using Kavita.Server.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Kavita.Server.Tests.Controllers;

public class ComicVineSettingsTests
{
    private static SettingsController Controller() => new(null!, null!, null!, null!, null!, null!, null!, null!, null!)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [Theory]
    [InlineData(nameof(SettingsController.GetComicVineSettings))]
    [InlineData(nameof(SettingsController.SaveComicVineSettings))]
    [InlineData(nameof(SettingsController.RemoveComicVineSettings))]
    [InlineData(nameof(SettingsController.TestComicVineConnection))]
    public void AllCredentialEndpoints_RequireAdminAndKavitaPlusAndAreNotCached(string name)
    {
        var method = typeof(SettingsController).GetMethod(name)!;
        Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PolicyGroups.AdminPolicy);
        Assert.NotNull(method.GetCustomAttribute<KPlusAttribute>());
        Assert.True(method.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore);
    }

    [Fact]
    public async Task Save_ReturnsOnlyStatus()
    {
        var service = Substitute.For<IComicVineSettingsService>();
        service.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new ComicVineSettingsDto(true, true, false, false));
        var result = await Controller().SaveComicVineSettings(new UpdateComicVineSettingsDto { ApiKey = "secret-api-key" }, service);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<ComicVineSettingsDto>(ok.Value);
        Assert.DoesNotContain("secret-api-key", JsonSerializer.Serialize(ok.Value));
        await service.Received(1).SaveAsync("secret-api-key", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad key")]
    public async Task InvalidKey_DoesNotReachStorage(string key)
    {
        var service = Substitute.For<IComicVineSettingsService>();
        var result = await Controller().SaveComicVineSettings(new UpdateComicVineSettingsDto { ApiKey = key }, service);
        Assert.IsType<BadRequestResult>(result.Result);
        await service.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnection_UsesTheConfiguredServerCredential()
    {
        var service = Substitute.For<IComicVineService>();
        service.TestConnectionAsync(Arg.Any<CancellationToken>()).Returns(true);
        var result = await Controller().TestComicVineConnection(service);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<ComicVineConnectionResultDto>(ok.Value).Success);
    }
}
