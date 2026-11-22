using System.ComponentModel.DataAnnotations;

namespace Kavita.Models.DTOs.Settings;

/// <summary>Credential status only. Never includes the saved or environment API key.</summary>
public sealed record ComicVineSettingsDto(bool HasApiKey, bool HasSavedApiKey, bool UsesEnvironmentKey,
    bool RequiresReplacement);

public sealed class UpdateComicVineSettingsDto
{
    [Required]
    [StringLength(256)]
    public string ApiKey { get; set; } = string.Empty;
}

public sealed record ComicVineConnectionResultDto(bool Success);
