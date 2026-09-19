using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;

namespace Our.Umbraco.ErrorDashboard.Reporting;

/// <summary>
///     Turns a wire envelope into a storable row, or rejects it.
/// </summary>
/// <remarks>
///     Pure and static on purpose. The collector is an unauthenticated public endpoint, so these
///     validation rules are the only thing standing between the internet and the database, and keeping
///     them free of I/O is what makes them testable without a browser or a server.
/// </remarks>
public static class NelReportParser
{
    /// <summary>Report family we accept. The Reporting API multiplexes other kinds down the same pipe.</summary>
    private const string NetworkErrorType = "network-error";

    /// <summary>The one NEL type that means "the request succeeded".</summary>
    private const string OkType = "ok";

    private static readonly string[] InterestingPrefixes = ["tls.", "dns.", "tcp."];

    /// <summary>
    ///     Validates one report and maps it to a row.
    /// </summary>
    /// <param name="envelope">A single report from the payload.</param>
    /// <param name="options">Current package configuration.</param>
    /// <param name="isAcceptedHost">
    ///     Decides whether a hostname belongs to this site. Injected rather than resolved here so the
    ///     parser stays free of I/O - the real implementation reads Umbraco's configured domains.
    /// </param>
    /// <param name="receivedUtc">Clock, passed in so the result is deterministic under test.</param>
    /// <returns>The row to persist, or null when the report is uninteresting or not ours.</returns>
    public static NelReportDto? TryMap(
        NelReportEnvelope? envelope,
        ErrorDashboardOptions options,
        Func<string, bool> isAcceptedHost,
        DateTime receivedUtc)
    {
        if (envelope?.Body is null)
        {
            return null;
        }

        if (!string.Equals(envelope.Type, NetworkErrorType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(envelope.Url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        // Without this the endpoint is a free logging service for anyone who finds it.
        if (!isAcceptedHost(uri.Host))
        {
            return null;
        }

        NelReportBody body = envelope.Body;
        string type = (body.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Length == 0)
        {
            return null;
        }

        bool isSuccess = type == OkType;
        if (!isSuccess && !IsInteresting(type, body.StatusCode))
        {
            return null;
        }

        // Sampled successes only exist to give the aggregator a traffic denominator. If nobody asked
        // for one, dropping them here keeps the raw table from filling with healthy traffic.
        if (isSuccess && options.SuccessFraction <= 0)
        {
            return null;
        }

        // A hostile or broken client can send any age it likes, including negative. Clamping rather
        // than rejecting keeps a report with a silly timestamp, just on the right day.
        long ageMs = Math.Clamp(envelope.Age, 0, (long)TimeSpan.FromDays(options.RawRetentionDays).TotalMilliseconds);
        DateTime occurredUtc = receivedUtc.AddMilliseconds(-ageMs);

        // Query strings are noisy - the same broken page appears once per tracking parameter - and they
        // are the part of a URL most likely to carry something personal.
        string path = Truncate(uri.AbsolutePath, 850);
        string host = Truncate(uri.Host, 255);

        double samplingFraction = body.SamplingFraction is > 0 and <= 1 ? body.SamplingFraction.Value : 1.0;

        return new NelReportDto
        {
            ReceivedUtc = receivedUtc,
            OccurredUtc = occurredUtc,
            Host = host,
            Path = path,
            Url = Truncate(uri.GetLeftPart(UriPartial.Path), 2048),
            UrlHash = Hash($"{host}{path}"),
            Type = Truncate(type, 100),
            Phase = Truncate((body.Phase ?? string.Empty).ToLowerInvariant(), 30),
            StatusCode = body.StatusCode is > 0 ? body.StatusCode : null,
            Method = TruncateOrNull(body.Method, 16),
            Protocol = TruncateOrNull(body.Protocol, 32),
            ServerIp = TruncateOrNull(body.ServerIp, 45),
            Referrer = TruncateOrNull(body.Referrer, 1024),
            ElapsedTimeMs = body.ElapsedTime is >= 0 ? body.ElapsedTime : null,
            SamplingFraction = samplingFraction,
            IsSuccess = isSuccess,
            Source = "nel",
        };
    }

    /// <summary>
    ///     Whether this error type is worth storing: any HTTP 4xx/5xx, plus every TLS, DNS and TCP
    ///     failure. An <c>http.error</c> carrying a 2xx or 3xx is a protocol oddity we do not track.
    /// </summary>
    public static bool IsInteresting(string type, int? statusCode)
    {
        if (type.StartsWith("http.", StringComparison.Ordinal))
        {
            return statusCode is >= 400;
        }

        return InterestingPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>SHA-256 hex. Fixed width, so it can carry an index where the URL itself cannot.</summary>
    public static string Hash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string? TruncateOrNull(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= max ? value : value[..max];
    }

    /// <summary>Midnight UTC of the day a timestamp falls in.</summary>
    public static DateTime ToUtcDay(DateTime value) => value.Date;

    /// <summary>Start of the UTC hour a timestamp falls in.</summary>
    public static DateTime ToUtcHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);

    /// <summary>Invariant round-trip formatting, used where a date goes into a SQL literal.</summary>
    public static string ToSqlDate(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
