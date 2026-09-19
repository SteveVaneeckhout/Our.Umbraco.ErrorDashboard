using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Our.Umbraco.ErrorDashboard.Persistence.Dtos;

/// <summary>
///     Errors per hour, per host, per type. Small and bounded, so it is kept effectively forever and
///     serves both the charts and the alert baseline. Daily figures are 24 of these summed - a separate
///     daily table would hold strictly less information for the same work.
/// </summary>
[TableName(TableName_)]
[PrimaryKey(nameof(Id))]
[ExplicitColumns]
public sealed class StatHourlyDto
{
    public const string TableName_ = "errorDashboardStatHourly";

    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public long Id { get; set; }

    /// <summary>Start of the UTC hour this row covers.</summary>
    [Column("bucketUtc")]
    [Index(IndexTypes.UniqueNonClustered, Name = "IX_errorDashboardStatHourly_bucket",
        ForColumns = "bucketUtc,host,type,statusCode")]
    public DateTime BucketUtc { get; set; }

    [Column("host")]
    [Length(255)]
    public string Host { get; set; } = string.Empty;

    [Column("type")]
    [Length(100)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Zero rather than null, so it can take part in the unique index above.</summary>
    [Column("statusCode")]
    public int StatusCode { get; set; }

    /// <summary>Reports actually received. The significance test uses this, never the weighted figure.</summary>
    [Column("rawCount")]
    public long RawCount { get; set; }

    /// <summary>Sum of 1/samplingFraction - an estimate of how many errors really occurred.</summary>
    [Column("weightedCount")]
    public double WeightedCount { get; set; }

    /// <summary>
    ///     Estimated total requests in this bucket, from sampled successes. Zero unless SuccessFraction
    ///     is configured above zero.
    /// </summary>
    [Column("estimatedRequests")]
    public double EstimatedRequests { get; set; }
}
