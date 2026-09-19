using System.Text.Json.Serialization;
using Our.Umbraco.ErrorDashboard.Alerting;

namespace Our.Umbraco.ErrorDashboard.ViewModels;

/// <summary>Which column the offending-pages table is sorted by.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PageSortField>))]
public enum PageSortField
{
    Count,
    LastSeen,
    Path,
}

/// <inheritdoc cref="PageSortField" />
[JsonConverter(typeof(JsonStringEnumConverter<SortDirection>))]
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>Everything the overview tab needs, in one round trip.</summary>
public class OverviewResponseModel
{
    /// <summary>One entry per day in the requested range, including days with no errors.</summary>
    public required IEnumerable<DailyPointResponseModel> Series { get; init; }

    /// <summary>Error totals grouped by NEL type, worst first.</summary>
    public required IEnumerable<ErrorTypeCountResponseModel> ByType { get; init; }

    /// <summary>Error totals grouped by HTTP status, worst first. Excludes reports with no status.</summary>
    public required IEnumerable<StatusCountResponseModel> ByStatus { get; init; }

    /// <summary>Errors in the rolling 24h window the alert test judges.</summary>
    public required long ObservedLast24Hours { get; init; }

    /// <summary>Current anomaly assessment, or null when there is not enough history yet.</summary>
    public AnomalyStateResponseModel? Anomaly { get; init; }

    /// <summary>Reports received but not yet rolled up, so the UI can say the figures are still settling.</summary>
    public required long PendingRawReports { get; init; }

    /// <summary>When the aggregator last advanced its watermark, or null if it has never run.</summary>
    public DateTime? LastAggregatedUtc { get; init; }

    /// <summary>Hosts that have reported at least once, for the host filter.</summary>
    public required IEnumerable<string> Hosts { get; init; }
}

/// <summary>One day of the overview chart.</summary>
public class DailyPointResponseModel
{
    public required DateTime Date { get; init; }

    public required long Count { get; init; }

    /// <summary>Estimated true error count once sampling is undone. Equals <c>Count</c> unless sampling is on.</summary>
    public required double WeightedCount { get; init; }

    /// <summary>Estimated total requests. Zero unless <c>SuccessFraction</c> is configured above zero.</summary>
    public required double EstimatedRequests { get; init; }
}

/// <summary>The detector's current verdict, surfaced so the dashboard can show the band, not just a number.</summary>
public class AnomalyStateResponseModel
{
    public required bool IsAnomalous { get; init; }

    public required double Observed { get; init; }

    /// <summary>Typical daily count over the baseline window.</summary>
    public required double Median { get; init; }

    /// <summary>Robust spread estimate the score is measured in.</summary>
    public required double Scale { get; init; }

    /// <summary>Modified z-score: how many times the site's normal variation this observation is.</summary>
    public required double Score { get; init; }

    public required int BaselineDays { get; init; }

    /// <summary>
    ///     Which gate decided the verdict. The dashboard turns this into a sentence in the editor's
    ///     own language; the server deliberately does not, because it does not know that language and
    ///     could not format the numbers for it if it did.
    /// </summary>
    public required AnomalyExplanation Explanation { get; init; }

    /// <summary>The figures behind the reason, in the order that reason's message expects them.</summary>
    public required IReadOnlyList<double> ExplanationArgs { get; init; }
}

/// <summary>Total for one NEL error type.</summary>
public class ErrorTypeCountResponseModel
{
    public required string Type { get; init; }

    public required long Count { get; init; }
}

/// <summary>Total for one HTTP status code.</summary>
public class StatusCountResponseModel
{
    public required int StatusCode { get; init; }

    public required long Count { get; init; }
}

/// <summary>A URL that visitors are hitting errors on.</summary>
public class OffendingPageResponseModel
{
    public required string Host { get; init; }

    public required string Path { get; init; }

    public required string Type { get; init; }

    /// <summary>HTTP status, or null for DNS, TCP and TLS failures which have none.</summary>
    public int? StatusCode { get; init; }

    public required long Count { get; init; }

    public required DateTime FirstSeenUtc { get; init; }

    public required DateTime LastSeenUtc { get; init; }

    /// <summary>
    ///     The content node this path resolves to, so the dashboard can deep-link to it. Null when
    ///     nothing matches, which for a 404 is usually the whole explanation.
    /// </summary>
    public Guid? DocumentKey { get; init; }

    /// <summary>
    ///     True when the path only resolved against unpublished content. On a 404 that is a far more
    ///     actionable finding than a missing page: something still links to a page that exists but is
    ///     not live.
    /// </summary>
    public bool IsUnpublished { get; init; }

    /// <summary>
    ///     Whether anything is known to link to this URL: a raw report inside the retention window
    ///     that carried a referrer. False covers both halves of "nothing to expand" - reports that
    ///     have been pruned (aggregates outlive them, so an old row can have counts and nothing else)
    ///     and reports that were all direct hits with no <c>Referer</c> header to record.
    /// </summary>
    public bool HasReferrers { get; init; }
}

/// <summary>A raw report, for confirming the pipeline works end to end.</summary>
public class RawReportResponseModel
{
    public required long Id { get; init; }

    public required DateTime ReceivedUtc { get; init; }

    public required DateTime OccurredUtc { get; init; }

    public required string Url { get; init; }

    public required string Type { get; init; }

    public required string Phase { get; init; }

    public int? StatusCode { get; init; }

    public string? Method { get; init; }

    public string? Protocol { get; init; }

    public string? ServerIp { get; init; }

    public string? Referrer { get; init; }

    public int? ElapsedTimeMs { get; init; }
}

/// <summary>The calling user's own alert subscription.</summary>
public class SubscriptionResponseModel
{
    public required bool Enabled { get; init; }

    /// <summary>
    ///     False when Umbraco has no usable SMTP configuration, so nothing would ever be delivered.
    ///     The dashboard explains that in the editor's own language; there is deliberately no message
    ///     here to go with it.
    /// </summary>
    public required bool CanReceiveEmail { get; init; }

    public DateTime? LastNotifiedUtc { get; init; }
}

/// <summary>Request body for changing the calling user's own subscription.</summary>
public class UpdateSubscriptionRequestModel
{
    public required bool Enabled { get; init; }
}

/// <summary>A subscribed user, for the admin list.</summary>
public class SubscriberResponseModel
{
    public required Guid UserKey { get; init; }

    public required string Name { get; init; }

    public required string Email { get; init; }

    /// <summary>
    ///     The user's backoffice UI language, so the alert email can be written in it. Null or empty
    ///     when they have never chosen one.
    /// </summary>
    public string? Language { get; init; }

    public DateTime? LastNotifiedUtc { get; init; }
}

/// <summary>A past alert.</summary>
public class AlertResponseModel
{
    public required int Id { get; init; }

    public required DateTime RaisedUtc { get; init; }

    public required DateTime WindowStartUtc { get; init; }

    public required string Host { get; init; }

    public required long Observed { get; init; }

    public required double Median { get; init; }

    public required double Score { get; init; }

    public required int RecipientCount { get; init; }

    public required IEnumerable<string> TopPaths { get; init; }
}

/// <summary>Effective configuration, so the dashboard can explain its own behaviour without guessing.</summary>
public class SettingsResponseModel
{
    public required bool Enabled { get; init; }

    public required string ReportPath { get; init; }

    public required double SuccessFraction { get; init; }

    public required double FailureFraction { get; init; }

    public required int RawRetentionDays { get; init; }

    /// <summary>Report payloads one client IP may send per minute before the collector returns 429.</summary>
    public required int RateLimitPerMinute { get; init; }

    /// <summary>
    ///     Hostnames the collector accepts reports about, from Umbraco domains plus configuration. Empty
    ///     means none are configured and it is falling back to the request Host header, which a caller
    ///     controls - worth surfacing rather than leaving implicit.
    /// </summary>
    public required IEnumerable<string> AcceptedHosts { get; init; }

    public required bool AlertsEnabled { get; init; }

    public required int BaselineDays { get; init; }

    public required int MinBaselineDays { get; init; }

    public required double ScoreThreshold { get; init; }

    public required int MinObserved { get; init; }

    public required double MinRatio { get; init; }

    public required int CooldownHours { get; init; }

    /// <summary>Whether Umbraco is actually able to send mail. False means alerts would vanish silently.</summary>
    public required bool CanSendEmail { get; init; }
}

/// <summary>Outcome of a manually triggered aggregation run.</summary>
public class RecomputeResponseModel
{
    public required int RowsAggregated { get; init; }

    public required int RowsPruned { get; init; }

    public required bool AlertRaised { get; init; }
}

/// <summary>A page that linked to a broken URL, and how often.</summary>
public class ReferrerResponseModel
{
    /// <summary>The linking page, or null when the browser sent no referrer.</summary>
    public string? Referrer { get; init; }

    public required long Count { get; init; }

    public required DateTime LastSeenUtc { get; init; }
}

/// <summary>Referrers for one broken URL, plus the window they could be drawn from.</summary>
public class ReferrersResponseModel
{
    public required IEnumerable<ReferrerResponseModel> Items { get; init; }

    /// <summary>
    ///     How far back referrers are actually available. Referrers live only in the raw reports table,
    ///     which is pruned, so this can be shorter than the range the caller asked for.
    /// </summary>
    public required int AvailableDays { get; init; }
}
