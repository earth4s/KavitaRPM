using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services.Metadata;
using Kavita.API.Services.Plus;
using Kavita.Common;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.Entities.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Metadata;

public sealed class ComicVineSettingsService(
    IUnitOfWork unitOfWork, IDataProtectionProvider dataProtection, ILogger<ComicVineSettingsService> logger,
    ILicenseService licenseService, Func<string?>? environmentKey = null) : IComicVineSettingsService
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Kavita.ComicVine.ApiKey.v1");

    public async Task<ComicVineSettingsDto> GetStatusAsync(CancellationToken ct = default)
    {
        await RequireLicense(ct);
        var setting = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey, ct);
        var hasSavedKey = !string.IsNullOrEmpty(setting?.Value);
        var savedKey = hasSavedKey ? Decrypt(setting!.Value) : null;
        var hasEnvironmentKey = !hasSavedKey && !string.IsNullOrWhiteSpace(EnvironmentKey());
        return new ComicVineSettingsDto(!string.IsNullOrWhiteSpace(savedKey) || hasEnvironmentKey,
            hasSavedKey, hasEnvironmentKey, hasSavedKey && string.IsNullOrWhiteSpace(savedKey));
    }

    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        // The singleton API client resolves credentials before cache reads and HTTP requests.
        // Apply this to saved and environment keys so background scans also require Kavita+.
        if (!await licenseService.HasActiveLicense(ct: ct)) return null;
        var setting = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey, ct);
        // An unreadable saved key must be replaced, rather than silently using a different credential.
        return string.IsNullOrEmpty(setting?.Value) ? EnvironmentKey()?.Trim() : Decrypt(setting.Value);
    }

    public async Task SaveAsync(string apiKey, CancellationToken ct = default)
    {
        await RequireLicense(ct);
        apiKey = apiKey.Trim();
        if (apiKey.Length is 0 or > 256 || apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Enter a valid Comic Vine API key.", nameof(apiKey));

        await unitOfWork.SettingsRepository.UpdateSettingAsync(ServerSettingKey.ComicVineApiKey,
            _protector.Protect(apiKey), ct);
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        await RequireLicense(ct);
        await unitOfWork.SettingsRepository.UpdateSettingAsync(ServerSettingKey.ComicVineApiKey, string.Empty, ct);
    }

    private async Task RequireLicense(CancellationToken ct)
    {
        if (!await licenseService.HasActiveLicense(ct: ct))
            throw new KavitaException("kavitaplus-restricted");
    }

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
            logger.LogWarning("[ComicVine] Saved API key is unavailable; replace it in Settings > Kavita+ > Metadata Providers > Comic Vine");
            #pragma warning restore S6667
            return null;
        }
    }
}
