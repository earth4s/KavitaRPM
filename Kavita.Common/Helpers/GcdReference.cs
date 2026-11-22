using System;
using System.Globalization;

namespace Kavita.Common.Helpers;

public sealed record GcdReference(bool Issue, int Id)
{
    public static GcdReference? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        string[] parts;
        if (text.StartsWith("gcd:", StringComparison.OrdinalIgnoreCase))
            parts = text[4..].Split(':');
        else
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Host is not ("comics.org" or "www.comics.org") || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
            var path = uri.AbsolutePath.Trim('/');
            if (path.StartsWith("api/", StringComparison.Ordinal)) path = path[4..];
            parts = path.Split('/');
        }
        if (parts.Length != 2 || parts[0] is not ("series" or "issue")
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        return new(parts[0] == "issue", id);
    }
}
