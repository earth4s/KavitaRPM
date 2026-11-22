using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Settings;

namespace Kavita.API.Services.Metadata;

public interface IComicVineSettingsService
{
    Task<ComicVineSettingsDto> GetStatusAsync(CancellationToken ct = default);
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);
    Task SaveAsync(string apiKey, CancellationToken ct = default);
    Task RemoveAsync(CancellationToken ct = default);
}
