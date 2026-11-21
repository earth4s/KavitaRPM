using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.Parser;

namespace Kavita.API.Services.Metadata;

public interface IGcdService : IComicCatalog
{
    Task<Kavita.Models.DTOs.KavitaPlus.Metadata.ExternalSeriesDetailDto?> FindSeriesAsync(string title, int? year,
        string? publisher, string? language, CancellationToken ct = default);
    Task<GcdIssueDetailDto?> GetIssueAsync(int id, CancellationToken ct = default);
    Task<GcdIssueDetailDto?> GetCoverIssueAsync(int id, bool issue, CancellationToken ct = default);
    Task<bool> EnrichAsync(IList<ParserInfo> infos, int? seriesId, CancellationToken ct = default);
    Task<GcdHealthDto> GetHealthAsync(CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}

public interface IGcdSettingsService
{
    Task<GcdSettingsDto> GetStatusAsync(CancellationToken ct);
    Task<string?> GetAuthorizationAsync(CancellationToken ct);
    Task SaveAsync(GcdCredentialsDto credentials, CancellationToken ct);
    Task RemoveAsync(CancellationToken ct);
}
