using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Our.Umbraco.ErrorDashboard.Persistence.Dtos;

/// <summary>
///     One raised alert. Doubles as the cooldown ledger - the alert service refuses to fire again for a
///     host while a recent row exists - and as the history shown on the dashboard, which is what lets
///     someone judge whether the thresholds are tuned right.
/// </summary>
[TableName(TableName_)]
[PrimaryKey(nameof(Id))]
[ExplicitColumns]
public sealed class AlertDto
{
    public const string TableName_ = "errorDashboardAlert";

    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("raisedUtc")]
    [Index(IndexTypes.NonClustered, Name = "IX_errorDashboardAlert_raisedUtc")]
    public DateTime RaisedUtc { get; set; }

    /// <summary>Start of the 24h window that tripped the test.</summary>
    [Column("windowStartUtc")]
    public DateTime WindowStartUtc { get; set; }

    [Column("host")]
    [Length(255)]
    public string Host { get; set; } = string.Empty;

    [Column("observed")]
    public long Observed { get; set; }

    [Column("median")]
    public double Median { get; set; }

    /// <summary>The robust scale estimate the score was divided by. Kept so a past decision can be re-explained.</summary>
    [Column("scale")]
    public double Scale { get; set; }

    [Column("score")]
    public double Score { get; set; }

    [Column("baselineDays")]
    public int BaselineDays { get; set; }

    [Column("recipientCount")]
    public int RecipientCount { get; set; }

    /// <summary>JSON array of the worst paths at the time, so the history row still means something later.</summary>
    [Column("topPathsJson")]
    [SpecialDbType(SpecialDbTypes.NVARCHARMAX)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? TopPathsJson { get; set; }
}
