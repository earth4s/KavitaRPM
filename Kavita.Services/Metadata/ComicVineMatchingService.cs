using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Kavita.API.Services.Plus;
using Kavita.API.Store;
using Kavita.Models.DTOs.KavitaPlus.Audit;
using Kavita.Models.Entities.Enums.Audit;
using Kavita.API.Database;
using Kavita.API.Repositories;
using Kavita.API.Services.Metadata;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs;
using Kavita.Models.DTOs.KavitaPlus.Manage;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Builders;
using Kavita.Models.Entities.Person;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Kavita.Services.Metadata;

public sealed class ComicVineMatchingService(IUnitOfWork unitOfWork, IComicVineService comicVine, IMapper mapper, IEventHub eventHub,
    IKavitaPlusAuditService audit, IUserContext userContext)
    : ComicMatchingService(unitOfWork, comicVine, mapper, eventHub, audit, userContext, MetadataProvider.ComicVine), IComicVineMatchingService {}

public sealed class GcdMatchingService(IUnitOfWork unitOfWork, IGcdService gcd, IMapper mapper, IEventHub eventHub,
    IKavitaPlusAuditService audit, IUserContext userContext)
    : ComicMatchingService(unitOfWork, gcd, mapper, eventHub, audit, userContext, MetadataProvider.Gcd), IGcdMatchingService {}

public abstract class ComicMatchingService(IUnitOfWork unitOfWork, IComicCatalog catalog, IMapper mapper, IEventHub eventHub,
    IKavitaPlusAuditService audit, IUserContext userContext, MetadataProvider provider)
{
    private bool IsGcd => provider == MetadataProvider.Gcd;

    private async Task<Series> ComicSeries(int seriesId, CancellationToken ct, bool includeMetadata = false)
    {
        var includes = SeriesIncludes.Library;
        if (includeMetadata) includes |= SeriesIncludes.Metadata;
        if (includeMetadata && IsGcd) includes |= SeriesIncludes.Chapters;
        var series = await unitOfWork.SeriesRepository.GetSeriesByIdAsync(seriesId, includes, ct);
        if (series == null) throw new KavitaException("Series not found.");
        if (series.Library.Type is not (LibraryType.Comic or LibraryType.ComicVine))
            throw new KavitaException("Direct comic matching is available for Comic and Comic (Flexible) libraries.");
        return series;
    }

    public async Task<MatchSeriesResultDto> SearchAsync(int seriesId, string query, CancellationToken ct = default)
    {
        var series = await ComicSeries(seriesId, ct);
        if (string.IsNullOrWhiteSpace(query))
            query = IsGcd ? (series.GcdId.HasValue ? $"gcd:series:{series.GcdId}" : series.Name)
                : !string.IsNullOrEmpty(series.ComicVineId) ? "comicvine:" + series.ComicVineId : series.Name;
        return await catalog.SearchVolumesAsync(query, ct);
    }

    public async Task ApplyAsync(int seriesId, int volumeId, CancellationToken ct = default)
    {
        var series = await ComicSeries(seriesId, ct, includeMetadata: true);
        var volume = await catalog.GetVolumeAsync(volumeId, ct);
        if (volume == null || (IsGcd ? volume.GcdId : volume.ComicVineId) != volumeId) throw new KavitaException("Series not found in this catalog.");
        if (IsGcd)
        {
            if (series.GcdId != volumeId)
                foreach (var chapter in series.Volumes.SelectMany(v => v.Chapters).Where(c => !c.IsSpecial))
                    chapter.GcdId = null;
            series.GcdId = volumeId;
        }
        else series.ComicVineId = volumeId.ToString(CultureInfo.InvariantCulture);
        series.MetadataProviderOverride = series.Library.MetadataProvider == provider
            ? null : provider;
        series.DontMatch = false;
        series.IsBlacklisted = false;
        await ApplyVolumeMetadata(unitOfWork, series, volume, ct);
        unitOfWork.SeriesRepository.Update(series);
        await unitOfWork.CommitAsync(ct);
        await audit.LogAsync(KavitaPlusAuditCategory.Match, KavitaPlusEventType.SeriesMatchFixed, AuditStatus.Success,
            AuditSubjectType.Series, seriesId, payload: new AuditLogMatchClearedParamsDto
            { SeriesName = series.Name, MatchedName = volume.Name }, userId: userContext.GetUserId(), ct: ct);
        await eventHub.SendMessageAsync(MessageFactory.SeriesUpdated, MessageFactory.SeriesUpdatedEvent(series.Id), ct: ct);
    }

    internal static async Task ApplyVolumeMetadata(IUnitOfWork unitOfWork, Series series, ExternalSeriesDetailDto volume, CancellationToken ct)
    {
        // An explicit match applies series-level data now, even when no issue can be scanned.
        // The scanner preserves these provider values; user locks still take precedence.
        var metadata = series.Metadata ??= new SeriesMetadataBuilder().Build();
        if (!metadata.SummaryLocked && !string.IsNullOrWhiteSpace(volume.Summary))
            metadata.Summary = volume.Summary;
        if (!metadata.LanguageLocked && !string.IsNullOrEmpty(volume.CatalogLanguage)) metadata.Language = volume.CatalogLanguage;
        if (!metadata.ReleaseYearLocked && volume.StartDate.HasValue)
            metadata.ReleaseYear = volume.StartDate.Value.Year;

        if (!metadata.PublisherLocked && !string.IsNullOrWhiteSpace(volume.Publisher))
        {
            var publisher = await unitOfWork.PersonRepository.GetPersonByNameOrAliasAsync(volume.Publisher, ct: ct)
                ?? new PersonBuilder(volume.Publisher).Build();
            foreach (var previous in metadata.People.Where(p => p.Role == PersonRole.Publisher).ToList())
                metadata.People.Remove(previous);
            metadata.People.Add(new SeriesMetadataPeople
            {
                Person = publisher, Role = PersonRole.Publisher, SeriesMetadata = metadata
            });
        }
        if (!string.IsNullOrWhiteSpace(volume.SiteUrl))
        {
            var links = metadata.WebLinks.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(link => !(volume.GcdId.HasValue ? GcdReference.TryParse(link) is { Issue: false } : IsComicVineVolumeLink(link))).Append(volume.SiteUrl).Distinct(StringComparer.Ordinal);
            metadata.WebLinks = string.Join(',', links);
        }
    }

    private static bool IsComicVineVolumeLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Host == "comicvine.gamespot.com"
        && uri.AbsolutePath.Split('/').Any(part => part.StartsWith("4050-", StringComparison.Ordinal));

    public async Task SetDontMatchAsync(int seriesId, bool dontMatch, CancellationToken ct = default)
    {
        var series = await ComicSeries(seriesId, ct);
        series.DontMatch = dontMatch;
        unitOfWork.SeriesRepository.Update(series);
        await unitOfWork.CommitAsync(ct);
        await audit.LogAsync(KavitaPlusAuditCategory.Match, KavitaPlusEventType.SeriesDontMatchSet, AuditStatus.Success,
            AuditSubjectType.Series, seriesId, payload: new AuditLogMatchDontMatchParamsDto
            { SeriesName = series.Name, DontMatch = dontMatch }, userId: userContext.GetUserId(), ct: ct);
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
            MatchStateOption.Matched => query.Where(s => !s.DontMatch && (IsGcd ? s.GcdId.HasValue : s.ComicVineId != null && s.ComicVineId != "")),
            MatchStateOption.NotMatched => query.Where(s => !s.DontMatch && (IsGcd ? !s.GcdId.HasValue : s.ComicVineId == null || s.ComicVineId == "")),
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
            return new ManageMatchSeriesDto { Series = dto, IsMatched = !s.DontMatch && (IsGcd ? s.GcdId.HasValue : !string.IsNullOrEmpty(s.ComicVineId)) };
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
            NotMatchedCount = await query.CountAsync(s => !s.DontMatch && (IsGcd ? !s.GcdId.HasValue : s.ComicVineId == null || s.ComicVineId == ""), ct)
        };
    }
}
