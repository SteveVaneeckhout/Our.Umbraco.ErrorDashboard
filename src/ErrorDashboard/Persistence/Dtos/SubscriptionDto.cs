using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Our.Umbraco.ErrorDashboard.Persistence.Dtos;

/// <summary>
///     A backoffice user's opt-in to alert emails. Keyed on the user's Guid rather than its integer id,
///     because the integer is not stable across a restore and the management API speaks Guids anyway.
/// </summary>
[TableName(TableName_)]
[PrimaryKey(nameof(UserKey), AutoIncrement = false)]
[ExplicitColumns]
public sealed class SubscriptionDto
{
    public const string TableName_ = "errorDashboardSubscription";

    [Column("userKey")]
    [PrimaryKeyColumn(AutoIncrement = false)]
    public Guid UserKey { get; set; }

    [Column("enabled")]
    public bool Enabled { get; set; }

    [Column("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [Column("lastNotifiedUtc")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public DateTime? LastNotifiedUtc { get; set; }
}
