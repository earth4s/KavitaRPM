using System.Text.Json;
using Kavita.API.Services.Plus;
using Kavita.Common;
using Kavita.Models.Parser;
using NSubstitute;
using Kavita.Database.Tests;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Metadata;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class ComicVineSettingsServiceTests(ITestOutputHelper output) : AbstractDbTest(output)
{
    private static ILicenseService ActiveLicense()
    {
        var license = Substitute.For<ILicenseService>();
        license.HasActiveLicense(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);
        return license;
    }

    [Fact]
    public async Task Save_ProtectsKeyAndKeepsItOutOfSettingsResponses()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var protector = new EphemeralDataProtectionProvider();
        var service = new ComicVineSettingsService(unitOfWork, protector,
            NullLogger<ComicVineSettingsService>.Instance, ActiveLicense(), () => null);
        await service.SaveAsync("  my-secret-comicvine-key  ");

        var stored = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey);
        Assert.NotEqual("my-secret-comicvine-key", stored.Value);
        Assert.DoesNotContain("my-secret-comicvine-key", stored.Value);
        Assert.Equal("my-secret-comicvine-key", await service.GetApiKeyAsync());
        var status = await service.GetStatusAsync();
        Assert.Equal(new ComicVineSettingsDto(true, true, false, false), status);
        Assert.DoesNotContain("my-secret-comicvine-key", JsonSerializer.Serialize(status));
        var generalSettings = JsonSerializer.Serialize(await unitOfWork.SettingsRepository.GetSettingsDtoAsync());
        Assert.DoesNotContain(stored.Value, generalSettings);
        Assert.DoesNotContain("my-secret-comicvine-key", generalSettings);

        // A new scoped service reads the same protected setting, rather than an in-memory copy.
        var nextScope = new ComicVineSettingsService(unitOfWork, protector,
            NullLogger<ComicVineSettingsService>.Instance, ActiveLicense(), () => null);
        Assert.Equal("my-secret-comicvine-key", await nextScope.GetApiKeyAsync());
    }

    [Fact]
    public async Task ReplaceAndRemove_ApplyImmediatelyAndRestoreEnvironmentFallback()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = new ComicVineSettingsService(unitOfWork, new EphemeralDataProtectionProvider(),
            NullLogger<ComicVineSettingsService>.Instance, ActiveLicense(), () => "environment-key");
        Assert.Equal(new ComicVineSettingsDto(true, false, true, false), await service.GetStatusAsync());
        await service.SaveAsync("first-key");
        Assert.Equal("first-key", await service.GetApiKeyAsync());
        await service.SaveAsync("replacement-key");
        Assert.Equal("replacement-key", await service.GetApiKeyAsync());
        Assert.False((await service.GetStatusAsync()).UsesEnvironmentKey);
        await service.RemoveAsync();
        Assert.Equal("environment-key", await service.GetApiKeyAsync());
        Assert.Equal(new ComicVineSettingsDto(true, false, true, false), await service.GetStatusAsync());
    }

    [Fact]
    public async Task RemoveWithoutEnvironmentKey_DisablesLookups()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = new ComicVineSettingsService(unitOfWork, new EphemeralDataProtectionProvider(),
            NullLogger<ComicVineSettingsService>.Instance, ActiveLicense(), () => null);
        await service.SaveAsync("saved-key");
        await service.RemoveAsync();
        Assert.Null(await service.GetApiKeyAsync());
        Assert.Equal(new ComicVineSettingsDto(false, false, false, false), await service.GetStatusAsync());
    }

    [Fact]
    public async Task UnreadableKey_RequiresReplacementWithoutSilentlyFallingBack()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        await unitOfWork.SettingsRepository.UpdateSettingAsync(ServerSettingKey.ComicVineApiKey, "unreadable-ciphertext");
        var service = new ComicVineSettingsService(unitOfWork, new EphemeralDataProtectionProvider(),
            NullLogger<ComicVineSettingsService>.Instance, ActiveLicense(), () => "environment-key");
        Assert.Null(await service.GetApiKeyAsync());
        Assert.Equal(new ComicVineSettingsDto(false, true, false, true), await service.GetStatusAsync());
        await service.SaveAsync("replacement-key");
        Assert.Equal("replacement-key", await service.GetApiKeyAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("key with spaces")]
    [InlineData("key\nwith-newline")]
    public async Task InvalidKey_IsRejectedWithoutChangingStoredValue(string key)
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = new ComicVineSettingsService(unitOfWork, new EphemeralDataProtectionProvider(),
            NullLogger<ComicVineSettingsService>.Instance, ActiveLicense(), () => null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(key));
        Assert.Null(await service.GetApiKeyAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveLicense_BlocksSettingsAndCachedApiUse_WithoutDeletingKeys(bool savedKey)
    {
        var (uow, _, _) = await CreateDatabase();
        var license = ActiveLicense();
        var settings = new ComicVineSettingsService(uow, new EphemeralDataProtectionProvider(),
            NullLogger<ComicVineSettingsService>.Instance, license, () => savedKey ? null : "test-key");
        if (savedKey) await settings.SaveAsync("test-key");
        var before = (await uow.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey))?.Value;
        var handler = new ComicVineServiceTests.Handler(_ => ComicVineServiceTests.Json(
            """{"status_code":1,"results":{"id":200,"name":"Example","publisher":{"name":"Publisher"}}}"""));
        using var api = new ComicVineService(new HttpClient(handler), "test-key",
            NullLogger<ComicVineService>.Instance, new ComicVineServiceTests.Clock(), settings.GetApiKeyAsync);
        Assert.NotNull(await api.GetVolumeAsync(200));
        var requestCount = handler.Requests.Count;

        // Revocation must block both fresh HTTP requests and previously cached metadata.
        license.HasActiveLicense(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(false);
        Assert.Null(await settings.GetApiKeyAsync());
        await Assert.ThrowsAsync<KavitaException>(() => settings.GetStatusAsync());
        await Assert.ThrowsAsync<KavitaException>(() => settings.SaveAsync("replacement"));
        await Assert.ThrowsAsync<KavitaException>(() => settings.RemoveAsync());
        Assert.False(await api.TestConnectionAsync());
        Assert.False(await api.EnrichAsync([new ParserInfo { Series = "Example", ComicVineId = "100" }], LibraryType.ComicVine, true));
        await Assert.ThrowsAsync<KavitaException>(() => api.GetVolumeAsync(200));
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.Equal(before, (await uow.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey))?.Value);

        license.HasActiveLicense(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);
        Assert.Equal("test-key", await settings.GetApiKeyAsync());
        Assert.NotNull(await api.GetVolumeAsync(200));
        Assert.Equal(requestCount, handler.Requests.Count); // Existing cache works again after activation.
    }
}
