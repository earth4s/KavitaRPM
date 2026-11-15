using System.Threading;
using System.Threading.Tasks;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs.KavitaPlus.Manage;
using Kavita.Models.DTOs.Metadata.Matching;

namespace Kavita.API.Services.Metadata;

public interface IComicVineMatchingService
{
    Task<MatchSeriesResultDto> SearchAsync(int seriesId, string query, CancellationToken ct = default);
    Task ApplyAsync(int seriesId, int volumeId, CancellationToken ct = default);
    Task SetDontMatchAsync(int seriesId, bool dontMatch, CancellationToken ct = default);
    Task<PagedList<ManageMatchSeriesDto>> ListAsync(ManageMatchFilterDto filter, UserParams pagination, CancellationToken ct = default);
    Task<MatchedExternalSeriesCountDto> CountsAsync(CancellationToken ct = default);
}
