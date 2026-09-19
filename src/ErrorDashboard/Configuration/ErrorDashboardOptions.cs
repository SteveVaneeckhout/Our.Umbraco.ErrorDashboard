namespace Our.Umbraco.ErrorDashboard.Configuration;

/// <summary>
///     Bound from the <c>ErrorDashboard</c> section of appsettings. Every value has a working default,
///     so the package does something sensible with no configuration at all.
/// </summary>
public sealed class ErrorDashboardOptions
{
    public const string SectionName = "ErrorDashboard";

    /// <summary>Master switch. When false no headers are emitted and the collector returns 404.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path the browser POSTs reports to. Must not collide with a content route.</summary>
    public string ReportPath { get; set; } = "/nel-report";

    /// <summary>How long the browser caches the NEL policy, in seconds. 30 days.</summary>
    public int MaxAgeSeconds { get; set; } = 2592000;

    /// <summary>Whether the policy also covers subdomains.</summary>
    public bool IncludeSubdomains { get; set; }

    /// <summary>Share of *failed* requests the browser reports. 1.0 means all of them.</summary>
    public double FailureFraction { get; set; } = 1.0;

    /// <summary>
    ///     Share of *successful* requests the browser reports. Zero by default, so healthy traffic costs
    ///     nothing. Raising it above zero is what gives the aggregator a traffic denominator, which in
    ///     turn allows rate-based rather than count-based alerting.
    /// </summary>
    public double SuccessFraction { get; set; }

    /// <summary>
    ///     Extra hostnames whose reports are accepted. The request's own host is always allowed; this is
    ///     only needed when a subdomain points its policy at this collector. Empty by default, which
    ///     stops the endpoint being a free logging service for the rest of the internet.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Hard cap on a report payload. Anything larger is rejected unparsed.</summary>
    public int MaxPayloadBytes { get; set; } = 65536;

    /// <summary>Reports beyond this many in a single payload are discarded.</summary>
    public int MaxReportsPerPayload { get; set; } = 100;

    /// <summary>Payloads accepted per client IP per minute before the collector starts returning 429.</summary>
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>
    ///     How long raw reports are kept. Must exceed <see cref="AlertOptions.BaselineDays" /> or the
    ///     aggregate history cannot be rebuilt after a bug.
    /// </summary>
    public int RawRetentionDays { get; set; } = 21;

    /// <summary>How long the per-URL daily breakdown is kept.</summary>
    public int UrlStatRetentionDays { get; set; } = 90;

    /// <summary>
    ///     Distinct paths tracked per day. Beyond this the tail is rolled into a single "(other)" row,
    ///     so a site being scanned for thousands of nonexistent URLs cannot blow the table up.
    /// </summary>
    public int TopPathsPerDay { get; set; } = 200;

    public AlertOptions Alerts { get; set; } = new();
}

/// <summary>Thresholds for the anomaly test. See <c>Alerting/AnomalyDetector.cs</c> for the maths.</summary>
public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Days of history the median and MAD are computed over.</summary>
    public int BaselineDays { get; set; } = 14;

    /// <summary>Below this many days of history nothing is ever alerted - the baseline is meaningless.</summary>
    public int MinBaselineDays { get; set; } = 7;

    /// <summary>Modified z-score at which an observation counts as anomalous. Iglewicz-Hoaglin use 3.5.</summary>
    public double ScoreThreshold { get; set; } = 3.5;

    /// <summary>Floor on the raw count, so a quiet site going from 0 to 3 errors never pages anyone.</summary>
    public int MinObserved { get; set; } = 10;

    /// <summary>Observed must be at least this multiple of the median. Kills marginal drift.</summary>
    public double MinRatio { get; set; } = 2.0;

    /// <summary>No second email for the same host inside this window.</summary>
    public int CooldownHours { get; set; } = 24;
}
