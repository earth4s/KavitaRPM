using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Kavita.API.Database;
using Kavita.API.Repositories;
using Kavita.API.Services.Metadata;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs;
using Kavita.Models.DTOs.KavitaPlus.Manage;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Kavita.Services.Metadata;

public sealed class ComicVineMatchingService(IUnitOfWork unitOfWork, IComicVineService comicVine, IMapper mapper)
    : IComicVineMatchingService
{
    private async Task<Series> ComicSeries(int seriesId, CancellationToken ct)
    {
        var series = await unitOfWork.SeriesRepository.GetSeriesByIdAsync(seriesId, SeriesIncludes.Library, ct);
        if (series == null) throw new KavitaException("Series not found.");
        if (series.Library.Type is not (LibraryType.Comic or LibraryType.ComicVine))
            throw new KavitaException("Comic Vine matching is available for Comic and Comic (Flexible) libraries.");
        return series;
    }

    public async Task<MatchSeriesResultDto> SearchAsync(int seriesId, string query, CancellationToken ct = default)
    {
        var series = await ComicSeries(seriesId, ct);
        if (string.IsNullOrWhiteSpace(query))
            query = !string.IsNullOrEmpty(series.ComicVineId) ? "comicvine:" + series.ComicVineId : series.Name;
        return await comicVine.SearchVolumesAsync(query, ct);
    }

    public async Task ApplyAsync(int seriesId, int volumeId, CancellationToken ct = default)
    {
        var series = await ComicSeries(seriesId, ct);
        var volume = await comicVine.GetVolumeAsync(volumeId, ct);
        if (volume?.ComicVineId != volumeId) throw new KavitaException("Comic Vine volume not found.");
        series.ComicVineId = volumeId.ToString(CultureInfo.InvariantCulture);
        series.MetadataProviderOverride = series.Library.MetadataProvider == MetadataProvider.ComicVine
            ? null : MetadataProvider.ComicVine;
        series.DontMatch = false;
        series.IsBlacklisted = false;
        unitOfWork.SeriesRepository.Update(series);
        await unitOfWork.CommitAsync(ct);
    }

    public async Task SetDontMatchAsync(int seriesId, bool dontMatch, CancellationToken ct = default)
    {
        var series = await ComicSeries(seriesId, ct);
        series.DontMatch = dontMatch;
        unitOfWork.SeriesRepository.Update(series);
        await unitOfWork.CommitAsync(ct);
    }

    private IQueryable<Series> Comics() => unitOfWork.DataContext.Series
        .Where(s => s.Library.Type == LibraryType.Comic || s.Library.Type == LibraryType.ComicVine);

    public async Task<PagedList<ManageMatchSeriesDto>> ListAsync(ManageMatchFilterDto filter, UserParams pagination,
        CancellationToken ct = default)
    {
        var query = Comics();
        if (filter.LibraryType >= 0) query = query.Where(s => s.Library.Type == (LibraryType)filter.LibraryType);
        if (!string.IsNullOrWhiteSpace(filter.SearchTerm)) query = query.Where(s => s.Name.Contains(filter.SearchTerm));
        query = filter.MatchStateOption switch
        {
            MatchStateOption.Matched => query.Where(s => !s.DontMatch && s.ComicVineId != null && s.ComicVineId != ""),
            MatchStateOption.NotMatched => query.Where(s => !s.DontMatch && (s.ComicVineId == null || s.ComicVineId == "")),
            MatchStateOption.DontMatch => query.Where(s => s.DontMatch),
            // A Kavita+ blacklist does not indicate a failed direct Comic Vine match.
            MatchStateOption.Error => query.Where(s => false),
            _ => query
        };
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, pagination.PageNumber);
        var size = Math.Clamp(pagination.PageSize, 1, 100);
        var rows = await query.Include(s => s.Library).Include(s => s.Metadata).AsNoTracking()
            .OrderBy(s => s.NormalizedName).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var result = rows.Select(s =>
        {
            var dto = mapper.Map<SeriesDto>(s);
            dto.IsBlacklisted = false;
            return new ManageMatchSeriesDto { Series = dto, IsMatched = !s.DontMatch && !string.IsNullOrEmpty(s.ComicVineId) };
        });
        return PagedList<ManageMatchSeriesDto>.Create(result, total, page, size);
    }

    public async Task<MatchedExternalSeriesCountDto> CountsAsync(CancellationToken ct = default)
    {
        var query = Comics();
        return new MatchedExternalSeriesCountDto
        {
            TotalCount = await query.CountAsync(ct),
            DontMatchCount = await query.CountAsync(s => s.DontMatch, ct),
            NotMatchedCount = await query.CountAsync(s => !s.DontMatch && (s.ComicVineId == null || s.ComicVineId == ""), ct)
        };
    }
}
