using System;
using System.ComponentModel.DataAnnotations;

namespace Kavita.Models.DTOs.Settings;

public sealed record GcdCredentialsDto([Required, StringLength(256)] string Username,
    [Required, StringLength(512)] string Password);
public sealed record GcdSettingsDto(bool Configured, bool RequiresReplacement);
public sealed record GcdHealthDto(bool Configured, int RequestsLastMinute, int RequestsLastDay,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? Failure, DateTimeOffset? RetryAt);

public sealed record GcdStoryDto(string Type, string Title, string Synopsis);
public sealed record GcdIssueDetailDto(int Id, int SeriesId, string Number, string Variant, int? VariantOf,
    string Isbn, string Barcode, string PublicationDate, string KeyDate, string OnSaleDate,
    string? CoverUrl, GcdStoryDto[] Stories);
