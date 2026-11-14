using System.Text.Json;
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
    [Fact]
    public async Task Save_ProtectsKeyAndKeepsItOutOfSettingsResponses()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var protector = new EphemeralDataProtectionProvider();
        var service = new ComicVineSettingsService(unitOfWork, protector,
            NullLogger<ComicVineSettingsService>.Instance, () => null);
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
            NullLogger<ComicVineSettingsService>.Instance, () => null);
        Assert.Equal("my-secret-comicvine-key", await nextScope.GetApiKeyAsync());
    }

    [Fact]
    public async Task ReplaceAndRemove_ApplyImmediatelyAndRestoreEnvironmentFallback()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var service = new ComicVineSettingsService(unitOfWork, new EphemeralDataProtectionProvider(),
            NullLogger<ComicVineSettingsService>.Instance, () => "environment-key");
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
            NullLogger<ComicVineSettingsService>.Instance, () => null);
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
            NullLogger<ComicVineSettingsService>.Instance, () => "environment-key");
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
            NullLogger<ComicVineSettingsService>.Instance, () => null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(key));
        Assert.Null(await service.GetApiKeyAsync());
    }
}
