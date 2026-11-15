using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services.Metadata;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.Entities.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Metadata;

public sealed class ComicVineSettingsService(
    IUnitOfWork unitOfWork, IDataProtectionProvider dataProtection, ILogger<ComicVineSettingsService> logger,
    Func<string?>? environmentKey = null) : IComicVineSettingsService
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Kavita.ComicVine.ApiKey.v1");

    public async Task<ComicVineSettingsDto> GetStatusAsync(CancellationToken ct = default)
    {
        var setting = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey, ct);
        var hasSavedKey = !string.IsNullOrEmpty(setting?.Value);
        var savedKey = hasSavedKey ? Decrypt(setting!.Value) : null;
        var hasEnvironmentKey = !hasSavedKey && !string.IsNullOrWhiteSpace(EnvironmentKey());
        return new ComicVineSettingsDto(!string.IsNullOrWhiteSpace(savedKey) || hasEnvironmentKey,
            hasSavedKey, hasEnvironmentKey, hasSavedKey && string.IsNullOrWhiteSpace(savedKey));
    }

    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        var setting = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey, ct);
        // An unreadable saved key must be replaced, rather than silently using a different credential.
        return string.IsNullOrEmpty(setting?.Value) ? EnvironmentKey()?.Trim() : Decrypt(setting.Value);
    }

    public async Task SaveAsync(string apiKey, CancellationToken ct = default)
    {
        apiKey = apiKey.Trim();
        if (apiKey.Length is 0 or > 256 || apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Enter a valid Comic Vine API key.", nameof(apiKey));

        await unitOfWork.SettingsRepository.UpdateSettingAsync(ServerSettingKey.ComicVineApiKey,
            _protector.Protect(apiKey), ct);
    }

    public Task RemoveAsync(CancellationToken ct = default) =>
        unitOfWork.SettingsRepository.UpdateSettingAsync(ServerSettingKey.ComicVineApiKey, string.Empty, ct);

    private string? EnvironmentKey() => environmentKey != null
        ? environmentKey() : Environment.GetEnvironmentVariable("KAVITA_COMICVINE_API_KEY");

    private string? Decrypt(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            #pragma warning disable S6667 // Credential failures must never log the protected value or exception.
            logger.LogWarning("[ComicVine] Saved API key is unavailable; replace it in Settings > Kavita+ > Meta API Connections > Comic Vine");
            #pragma warning restore S6667
            return null;
        }
    }
}
