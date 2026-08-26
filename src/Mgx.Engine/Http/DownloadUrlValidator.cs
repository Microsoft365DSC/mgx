namespace Mgx.Engine.Http;

/// <summary>
/// Validates a pre-authenticated content download URL before the token-free client fetches it.
/// The allowlist keeps that client pointed at Microsoft-operated infrastructure. It does not
/// vouch for the content, since any tenant controls a *.sharepoint.com subdomain.
/// </summary>
public static class DownloadUrlValidator
{
    // Leading dot means "some subdomain of", so evilsharepoint.com does not match
    private static readonly string[] AllowedHostSuffixes =
    [
        ".sharepoint.com",
        ".sharepoint.us",
        ".sharepoint.cn",
        ".sharepointonline.com",
        ".files.1drv.com",
        ".svc.ms"
    ];

    /// <summary>
    /// Returns the URL unchanged when it passes every check, or null when the download must be
    /// refused. Refusal covers a non-HTTPS scheme, a non-default port, embedded userinfo and a
    /// host outside the allowlist.
    /// </summary>
    public static string? Validate(string? downloadUrl)
    {
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return null;

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!uri.IsDefaultPort) return null;

        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;

        // IdnHost is punycode-normalized, so lookalike Unicode hosts compare in the alphabet
        // the resolver uses
        var host = uri.IdnHost;
        foreach (var suffix in AllowedHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return downloadUrl;
        }

        return null;
    }
}
