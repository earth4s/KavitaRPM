using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Parser;

namespace Kavita.API.Services.Metadata;

public interface IComicVineService
{
    bool IsEnabled { get; }

    /// <summary>
    /// Fills missing issue metadata using explicit Comic Vine IDs, without changing file grouping.
    /// Disabled unless the host supplies its own API key. Does not require Kavita+.
    /// </summary>
    Task EnrichAsync(IList<ParserInfo> infos, LibraryType libraryType, bool enableMetadata,
        CancellationToken ct = default);
}
