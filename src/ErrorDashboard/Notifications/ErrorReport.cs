using Our.Umbraco.ErrorDashboard.Persistence.Dtos;

namespace Our.Umbraco.ErrorDashboard.Notifications;

/// <summary>
///     One stored network error report, as handed to <see cref="ErrorReportsReceivedNotification" />
///     handlers.
/// </summary>
/// <remarks>
///     A read-only copy rather than the persistence row, so a handler can neither change what was
///     stored nor come to depend on the table layout. Like the row, it holds nothing identifying about
///     the visitor.
/// </remarks>
public sealed record ErrorReport
{
    /// <summary>When the collector accepted the report.</summary>
    public required DateTime ReceivedUtc { get; init; }

    /// <summary>
    ///     When the error actually happened - receipt time minus the report's <c>age</c>. Use this, not
    ///     <see cref="ReceivedUtc" />, as the timestamp of anything you chart: reports arrive minutes late.
    /// </summary>
    public required DateTime OccurredUtc { get; init; }

    public required string Host { get; init; }

    /// <summary>
    ///     Path only, query string stripped. Unbounded cardinality on a site full of broken links, so it
    ///     belongs in a log line rather than a metric label.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Scheme, host, port and path - the query string is stripped here too.</summary>
    public required string Url { get; init; }

    /// <summary>NEL error type, e.g. <c>http.error</c>, <c>tls.cert.date_invalid</c>, <c>dns.name_not_resolved</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Which stage failed: <c>dns</c>, <c>connection</c> or <c>application</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>HTTP status where there was one. Null for DNS, TCP and TLS failures.</summary>
    public int? StatusCode { get; init; }

    public string? Method { get; init; }

    /// <summary>ALPN identifier - <c>h2</c>, <c>http/1.1</c>, and so on.</summary>
    public string? Protocol { get; init; }

    /// <summary>The site's own address as the browser resolved it - not the visitor's.</summary>
    public string? ServerIp { get; init; }

    public string? Referrer { get; init; }

    public int? ElapsedTimeMs { get; init; }

    /// <summary>The sampling rate that was in force, in (0, 1].</summary>
    public double SamplingFraction { get; init; } = 1.0;

    /// <summary>
    ///     How many real requests this report stands for: the reciprocal of
    ///     <see cref="SamplingFraction" />. Add this to a counter rather than 1, or a <c>FailureFraction</c>
    ///     below 1 quietly divides every figure you chart.
    /// </summary>
    public double Weight => 1.0 / SamplingFraction;

    /// <summary>
    ///     True for a sampled <em>success</em>, which only arrives when <c>SuccessFraction</c> is above
    ///     zero. Filter these out unless you want them as a traffic denominator.
    /// </summary>
    public bool IsSuccess { get; init; }

    internal static ErrorReport FromDto(NelReportDto dto) =>
        new()
        {
            ReceivedUtc = dto.ReceivedUtc,
            OccurredUtc = dto.OccurredUtc,
            Host = dto.Host,
            Path = dto.Path,
            Url = dto.Url,
            Type = dto.Type,
            Phase = dto.Phase,
            StatusCode = dto.StatusCode,
            Method = dto.Method,
            Protocol = dto.Protocol,
            ServerIp = dto.ServerIp,
            Referrer = dto.Referrer,
            ElapsedTimeMs = dto.ElapsedTimeMs,
            SamplingFraction = dto.SamplingFraction,
            IsSuccess = dto.IsSuccess,
        };
}
