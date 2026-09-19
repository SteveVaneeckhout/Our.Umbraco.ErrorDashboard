namespace Our.Umbraco.ErrorDashboard.Reporting;

/// <summary>Decides whether a report claiming to come from a given host is one of ours.</summary>
public interface IReportHostValidator
{
    /// <summary>
    ///     Whether reports about <paramref name="reportHost" /> should be stored.
    /// </summary>
    /// <param name="reportHost">Host parsed out of the report's own <c>url</c>.</param>
    /// <param name="requestHost">Host the payload was POSTed to. Caller-supplied, so barely trusted.</param>
    bool IsAccepted(string reportHost, string requestHost);

    /// <summary>
    ///     The hosts currently accepted, or an empty list when nothing authoritative is configured and
    ///     the validator is falling back to the request's own host. Surfaced on the dashboard so the
    ///     weaker fallback mode is visible rather than silent.
    /// </summary>
    IReadOnlyList<string> KnownHosts { get; }
}
