using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Our.Umbraco.ErrorDashboard.Persistence.Dtos;

/// <summary>
///     One network error report as the browser sent it, normalised. Rows here are transient: the
///     aggregator rolls them into the stat tables and then prunes them past the retention window.
/// </summary>
/// <remarks>
///     Deliberately holds nothing identifying. <c>ServerIp</c> is *our* address as the browser resolved
///     it, not the visitor's, and neither the client IP nor the user-agent is stored.
/// </remarks>
[TableName(TableName_)]
[PrimaryKey(nameof(Id))]
[ExplicitColumns]
public sealed class NelReportDto
{
    public const string TableName_ = "errorDashboardNelReport";

    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public long Id { get; set; }

    /// <summary>When the collector accepted the report.</summary>
    [Column("receivedUtc")]
    [Index(IndexTypes.NonClustered, Name = "IX_errorDashboardNelReport_receivedUtc")]
    public DateTime ReceivedUtc { get; set; }

    /// <summary>
    ///     When the error actually happened - receipt time minus the report's <c>age</c>. Everything
    ///     buckets on this, because reports can arrive minutes late and bucketing on receipt would
    ///     manufacture a spike whenever a batch lands.
    /// </summary>
    [Column("occurredUtc")]
    [Index(IndexTypes.NonClustered, Name = "IX_errorDashboardNelReport_occurredUtc")]
    public DateTime OccurredUtc { get; set; }

    [Column("host")]
    [Length(255)]
    public string Host { get; set; } = string.Empty;

    /// <summary>Path only, query string stripped - query strings are noisy and often carry personal data.</summary>
    [Column("path")]
    [Length(850)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Full URL, for display. Too long to index; use <see cref="UrlHash" /> for that.</summary>
    [Column("url")]
    [Length(2048)]
    public string Url { get; set; } = string.Empty;

    /// <summary>SHA-256 of host+path, hex. Fixed width, so it can carry an index where the URL cannot.</summary>
    [Column("urlHash")]
    [Length(64)]
    [Index(IndexTypes.NonClustered, Name = "IX_errorDashboardNelReport_urlHash")]
    public string UrlHash { get; set; } = string.Empty;

    /// <summary>NEL error type, e.g. <c>http.error</c>, <c>tls.cert.date_invalid</c>, <c>dns.name_not_resolved</c>.</summary>
    [Column("type")]
    [Length(100)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Which stage failed: <c>dns</c>, <c>connection</c> or <c>application</c>.</summary>
    [Column("phase")]
    [Length(30)]
    public string Phase { get; set; } = string.Empty;

    /// <summary>HTTP status where there was one. Null for DNS, TCP and TLS failures.</summary>
    [Column("statusCode")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public int? StatusCode { get; set; }

    [Column("method")]
    [Length(16)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Method { get; set; }

    /// <summary>ALPN identifier - <c>h2</c>, <c>http/1.1</c>, and so on.</summary>
    [Column("protocol")]
    [Length(32)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Protocol { get; set; }

    [Column("serverIp")]
    [Length(45)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? ServerIp { get; set; }

    [Column("referrer")]
    [Length(1024)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Referrer { get; set; }

    [Column("elapsedTimeMs")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public int? ElapsedTimeMs { get; set; }

    /// <summary>
    ///     The sampling rate that was in force. Stored per row so the fractions can be retuned without
    ///     invalidating history - weighted counts are the sum of its reciprocal.
    /// </summary>
    [Column("samplingFraction")]
    public double SamplingFraction { get; set; } = 1.0;

    /// <summary>True for sampled *successes*, which only arrive when SuccessFraction is above zero.</summary>
    [Column("isSuccess")]
    public bool IsSuccess { get; set; }

    /// <summary>Where the row came from. Always <c>nel</c> today; here so a server-side collector can share the table.</summary>
    [Column("source")]
    [Length(20)]
    public string Source { get; set; } = "nel";
}
