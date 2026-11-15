using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.DTOs.KavitaPlus.Metadata;

namespace Kavita.API.Services.Metadata;

public interface IComicVineService
{
    /// <summary>
    /// Fills missing issue metadata using explicit Comic Vine IDs, without changing file grouping.
    /// Returns whether direct lookups are configured for this library. Does not require Kavita+.
    /// </summary>
    Task<bool> EnrichAsync(IList<ParserInfo> infos, LibraryType libraryType, bool enableMetadata,
        CancellationToken ct = default);

    Task<bool> TestConnectionAsync(CancellationToken ct = default);
    Task<MatchSeriesResultDto> SearchVolumesAsync(string query, CancellationToken ct = default);
    Task<ExternalSeriesDetailDto?> GetVolumeAsync(int volumeId, CancellationToken ct = default);
}
