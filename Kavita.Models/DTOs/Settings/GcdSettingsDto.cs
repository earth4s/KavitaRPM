using System;
using System.ComponentModel.DataAnnotations;

namespace Kavita.Models.DTOs.Settings;

public sealed record GcdCredentialsDto([Required, StringLength(256)] string Username,
    [Required, StringLength(512)] string Password);
public sealed record GcdSettingsDto(bool Configured, bool RequiresReplacement);
public sealed record GcdHealthDto(bool Configured, int RequestsLastMinute, int RequestsLastDay,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? Failure, DateTimeOffset? RetryAt);
