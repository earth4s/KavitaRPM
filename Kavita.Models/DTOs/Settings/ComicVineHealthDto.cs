using System;

#nullable enable
namespace Kavita.Models.DTOs.Settings;

public sealed record ComicVineHealthDto(bool Configured, int RequestsLastHour, int RequestLimit,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? Failure, DateTimeOffset? RetryAt);
