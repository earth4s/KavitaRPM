using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Hangfire;
using Kavita.API.Services.Metadata;
using Kavita.API.Services.Scanner;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.DTOs.KavitaPlus.Manage;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Server.Attributes;
using Kavita.Server.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kavita.Server.Controllers;

[Authorize(Policy = PolicyGroups.AdminPolicy)]
[KPlus]
public sealed class GcdController(IGcdMatchingService matching) : BaseApiController
{
    [HttpGet("settings")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<GcdSettingsDto>> Settings([FromServices] IGcdSettingsService settings) =>
        Ok(await settings.GetStatusAsync(HttpContext.RequestAborted));

    [HttpPut("settings")]
    public async Task<ActionResult<GcdSettingsDto>> Save([FromServices] IGcdSettingsService settings, GcdCredentialsDto dto)
    {
        await settings.SaveAsync(dto, HttpContext.RequestAborted);
        return Ok(await settings.GetStatusAsync(HttpContext.RequestAborted));
    }

    [HttpDelete("settings")]
    public async Task<IActionResult> Remove([FromServices] IGcdSettingsService settings)
    {
        await settings.RemoveAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("test")]
    public async Task<ActionResult<bool>> Test([FromServices] IGcdService service) =>
        Ok(await service.TestConnectionAsync(HttpContext.RequestAborted));

    [SeriesAccess]
    [HttpPost("search")]
    public async Task<ActionResult<MatchSeriesResultDto>> Search([FromQuery] int seriesId, ComicVineSearchDto dto)
    {
        try { return Ok(await matching.SearchAsync(seriesId, dto.Query ?? string.Empty, HttpContext.RequestAborted)); }
        catch (KavitaException ex) { return BadRequest(ex.Message); }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or OperationCanceledException)
                                   && !HttpContext.RequestAborted.IsCancellationRequested)
        { return StatusCode(503, "Unable to contact GCD. Check your credentials or try again later."); }
    }

    [SeriesAccess]
    [HttpPost("match")]
    public async Task<ActionResult> Apply([FromQuery] int seriesId, ComicVineMatchDto dto)
    {
        try
        {
            await matching.ApplyAsync(seriesId, dto.VolumeId, HttpContext.RequestAborted);
            BackgroundJob.Enqueue<IScannerService>(scanner => scanner.ScanSeries(seriesId, true));
            return Ok();
        }
        catch (KavitaException ex) { return BadRequest(ex.Message); }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or OperationCanceledException)
                                   && !HttpContext.RequestAborted.IsCancellationRequested)
        { return StatusCode(503, "Unable to contact GCD. Check your credentials or try again later."); }
    }

    [SeriesAccess]
    [HttpPost("dont-match")]
    public async Task<ActionResult> DontMatch([FromQuery] int seriesId, [FromQuery] bool dontMatch)
    {
        try { await matching.SetDontMatchAsync(seriesId, dontMatch, HttpContext.RequestAborted); return Ok(); }
        catch (KavitaException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("health")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<GcdHealthDto>> Health([FromServices] IGcdService service) =>
        Ok(await service.GetHealthAsync(HttpContext.RequestAborted));

    [HttpPost("series")]
    public async Task<ActionResult<PagedList<ManageMatchSeriesDto>>> Series(ManageMatchFilterDto filter, [FromQuery] UserParams pagination)
    {
        var result = await matching.ListAsync(filter, pagination, HttpContext.RequestAborted);
        Response.AddPaginationHeader(result);
        return Ok(result);
    }

    [HttpGet("counts")]
    public async Task<ActionResult<MatchedExternalSeriesCountDto>> Counts() =>
        Ok(await matching.CountsAsync(HttpContext.RequestAborted));
}
