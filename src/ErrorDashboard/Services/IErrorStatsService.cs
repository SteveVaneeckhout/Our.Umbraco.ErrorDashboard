using Our.Umbraco.ErrorDashboard.ViewModels;
using Umbraco.Cms.Api.Common.ViewModels.Pagination;

namespace Our.Umbraco.ErrorDashboard.Services;

/// <summary>
///     Rolls raw reports into the aggregate tables, and answers the dashboard's questions from them.
/// </summary>
public interface IErrorStatsService
{
    /// <summary>
    ///     Rolls every raw report received since the last run into the hourly and per-URL tables.
    /// </summary>
    /// <returns>How many raw rows were folded in.</returns>
    Task<int> AggregateAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes raw reports and per-URL rows past their retention windows.</summary>
    /// <returns>How many rows were removed.</returns>
    Task<int> PruneAsync(CancellationToken cancellationToken = default);

    /// <summary>Everything the overview tab renders.</summary>
    Task<OverviewResponseModel> GetOverviewAsync(int days, string? host, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Daily error counts for the complete days preceding <paramref name="beforeUtc" />, oldest
    ///     first. This is the alert baseline.
    /// </summary>
    Task<IReadOnlyList<double>> GetDailyBaselineAsync(
        string? host,
        DateTime beforeUtc,
        int days,
        CancellationToken cancellationToken = default);

    /// <summary>Error count in the rolling window ending now. The figure the alert test judges.</summary>
    Task<long> GetWindowCountAsync(string? host, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Hosts that have reported at least once.</summary>
    Task<IReadOnlyList<string>> GetHostsAsync(CancellationToken cancellationToken = default);

    /// <summary>URLs visitors are hitting errors on, worst first.</summary>
    Task<PagedViewModel<OffendingPageResponseModel>> GetOffendingPagesAsync(
        DateTime fromUtc,
        DateTime toUtc,
        string? host,
        string? type,
        int? statusCode,
        int skip,
        int take,
        PageSortField sortField,
        SortDirection direction,
        CancellationToken cancellationToken = default);

    /// <summary>The worst paths in a window, as bare strings. Used to give an alert email some substance.</summary>
    Task<IReadOnlyList<string>> GetTopPathsAsync(
        string host,
        DateTime fromUtc,
        DateTime toUtc,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Pages that linked to one broken URL, most frequent first — the answer to "what is still
    ///     pointing at this 404".
    /// </summary>
    /// <remarks>
    ///     Read from the raw reports table, since referrers are deliberately not aggregated, so the
    ///     window is bounded by <c>RawRetentionDays</c> however far back the caller asks.
    /// </remarks>
    Task<ReferrersResponseModel> GetReferrersAsync(
        string host,
        string path,
        int days,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Raw reports, newest first. Exists to confirm the pipeline works end to end.</summary>
    Task<PagedViewModel<RawReportResponseModel>> GetRawReportsAsync(
        string? host,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
