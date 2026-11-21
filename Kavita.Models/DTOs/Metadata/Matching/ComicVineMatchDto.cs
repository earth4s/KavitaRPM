using System.ComponentModel.DataAnnotations;

namespace Kavita.Models.DTOs.Metadata.Matching;

public sealed class ComicVineSearchDto
{
    [StringLength(200)]
    public string Query { get; set; } = string.Empty;
}

public sealed class ComicVineMatchDto
{
    [Range(1, int.MaxValue)]
    public int VolumeId { get; set; }
}
