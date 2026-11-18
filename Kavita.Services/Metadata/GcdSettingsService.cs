using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services.Metadata;
using Kavita.API.Services.Plus;
using Kavita.Common;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.Entities.Enums;
using Microsoft.AspNetCore.DataProtection;

namespace Kavita.Services.Metadata;

public sealed class GcdSettingsService(IUnitOfWork unitOfWork, IDataProtectionProvider protection, ILicenseService license)
    : IGcdSettingsService
{
    private readonly IDataProtector _protector = protection.CreateProtector("Kavita.GCD.Credentials.v1");

    public async Task<string?> GetAuthorizationAsync(CancellationToken ct)
    {
        if (!await license.HasActiveLicense(ct: ct)) return null;
        var setting = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.GcdCredentials, ct);
        return Unprotect(setting?.Value);
    }

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return _protector.Unprotect(value); }
        catch (CryptographicException) { return null; }
    }

    private async Task RequireLicense(CancellationToken ct)
    {
        if (!await license.HasActiveLicense(ct: ct)) throw new KavitaException("kavitaplus-restricted");
    }

    public async Task<GcdSettingsDto> GetStatusAsync(CancellationToken ct)
    {
        await RequireLicense(ct);
        var setting = await unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.GcdCredentials, ct);
        var configured = Unprotect(setting?.Value) != null;
        return new(configured, !configured && !string.IsNullOrEmpty(setting?.Value));
    }

    public async Task SaveAsync(GcdCredentialsDto credentials, CancellationToken ct)
    {
        await RequireLicense(ct);
        var user = credentials.Username.Trim();
        if (user.Length is 0 or > 256 || user.Contains(':') || user.Any(char.IsControl)
            || credentials.Password.Length is 0 or > 512 || credentials.Password.Any(char.IsControl))
            throw new KavitaException("Enter a valid GCD username and password.");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + credentials.Password));
        await unitOfWork.SettingsRepository.UpdateSettingAsync(ServerSettingKey.GcdCredentials, _protector.Protect(auth), ct);
    }

    public async Task RemoveAsync(CancellationToken ct)
    {
        await RequireLicense(ct);
        await unitOfWork.SettingsRepository.UpdateSettingAsync(ServerSettingKey.GcdCredentials, string.Empty, ct);
    }
}
