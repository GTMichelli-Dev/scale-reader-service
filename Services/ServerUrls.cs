namespace ScaleReaderService.Services;

/// <summary>
/// The Foundation sites this service works for. ServerUrl holds one address,
/// or several - one per site - separated by commas, semicolons, spaces or new
/// lines: "https://agnw.scaledata.net, https://crs.scaledata.net". A scale
/// several companies share is set up once and serves them all; each site sees
/// it as its own and is answered on its own connection.
///
/// One address behaves exactly as the single ServerUrl always did.
/// </summary>
public static class ServerUrls
{
    private static readonly char[] Separators = { ',', ';', ' ', '\n', '\r', '\t' };

    /// <summary>Each well-formed http(s) address once, without a trailing slash,
    /// in the order given. Anything else is left out; see <see cref="Rejected"/>.</summary>
    public static List<string> Parse(string? value) =>
        Split(value).Where(IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The entries <see cref="Parse"/> left out, so they can be logged.</summary>
    public static List<string> Rejected(string? value) =>
        Split(value).Where(u => !IsValid(u)).ToList();

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.TrimEnd('/'));

    private static bool IsValid(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
