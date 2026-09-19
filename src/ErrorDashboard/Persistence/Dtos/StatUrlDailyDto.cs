using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Our.Umbraco.ErrorDashboard.Persistence.Dtos;

/// <summary>
///     Per-URL daily breakdown, which is what makes the dashboard actionable - "which pages are
///     broken", not just "how many errors". Capped at the top N paths per day; the tail collapses into
///     a single <see cref="OtherPath" /> row.
/// </summary>
[TableName(TableName_)]
[PrimaryKey(nameof(Id))]
[ExplicitColumns]
public sealed class StatUrlDailyDto
{
    public const string TableName_ = "errorDashboardStatUrlDaily";

    /// <summary>Placeholder path the long tail is rolled into once the daily cap is reached.</summary>
    public const string OtherPath = "(other)";

    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public long Id { get; set; }

    /// <summary>Midnight UTC of the day this row covers.</summary>
    [Column("date")]
    [Index(IndexTypes.UniqueNonClustered, Name = "IX_errorDashboardStatUrlDaily_key",
        ForColumns = "date,host,pathHash,type,statusCode")]
    public DateTime Date { get; set; }

    [Column("host")]
    [Length(255)]
    public string Host { get; set; } = string.Empty;

    /// <summary>SHA-256 of the path, hex. Indexed in place of the path, which is too wide for a key.</summary>
    [Column("pathHash")]
    [Length(64)]
    public string PathHash { get; set; } = string.Empty;

    [Column("path")]
    [Length(850)]
    public string Path { get; set; } = string.Empty;

    [Column("type")]
    [Length(100)]
    public string Type { get; set; } = string.Empty;

    [Column("statusCode")]
    public int StatusCode { get; set; }

    [Column("rawCount")]
    public long RawCount { get; set; }

    [Column("weightedCount")]
    public double WeightedCount { get; set; }

    [Column("firstSeenUtc")]
    public DateTime FirstSeenUtc { get; set; }

    [Column("lastSeenUtc")]
    public DateTime LastSeenUtc { get; set; }
}
