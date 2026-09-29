namespace Tickwire.Api.Services;

public sealed class TickwireOptions
{
    public int FixPort { get; set; } = 9878;
    public string VenueCompId { get; set; } = "TICKWIRE";

    /// <summary>Host name printed in generated client configs (the public FIX endpoint).</summary>
    public string PublicFixHost { get; set; } = "localhost";

    /// <summary>Key for admin endpoints (X-Admin-Key header). Empty disables admin endpoints.</summary>
    public string AdminKey { get; set; } = string.Empty;

    public int GuestTtlHours { get; set; } = 24;

    /// <summary>Guest (and FIX credential) provisioning allowed per client IP per minute.</summary>
    public int GuestsPerMinute { get; set; } = 20;

    /// <summary>Guest sessions use a short heartbeat so TestRequest/timeout chaos is visible within seconds.</summary>
    public int GuestHeartBtInt { get; set; } = 10;

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Origins with one '*' wildcard, e.g. https://tickwire-*.vercel.app for preview deployments.</summary>
    public string[] AllowedOriginPatterns { get; set; } = [];

    public bool IsOriginAllowed(string origin)
    {
        if (AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var pattern in AllowedOriginPatterns)
        {
            var star = pattern.IndexOf('*', StringComparison.Ordinal);
            if (star < 0)
            {
                continue;
            }

            var prefix = pattern[..star];
            var suffix = pattern[(star + 1)..];
            if (origin.Length > prefix.Length + suffix.Length
                && origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && origin.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && !origin.AsSpan(prefix.Length, origin.Length - prefix.Length - suffix.Length).ContainsAny('/', '.', ':'))
            {
                return true;
            }
        }

        return false;
    }
}
