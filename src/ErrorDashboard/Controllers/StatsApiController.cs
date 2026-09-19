using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Our.Umbraco.ErrorDashboard.Services;
using Our.Umbraco.ErrorDashboard.ViewModels;
using Umbraco.Cms.Api.Common.ViewModels.Pagination;

namespace Our.Umbraco.ErrorDashboard.Controllers;

/// <summary>Read-only statistics for the overview and offending-pages dashboards.</summary>
[ApiVersion("1.0")]
[ApiExplorerSettings(GroupName = "ErrorDashboard")]
public class StatsApiController : ErrorDashboardApiControllerBase
{
    /// <summary>Longest range the overview will chart. A year of daily points is already unreadable.</summary>
    private const int MaxDays = 365;

    private readonly IErrorStatsService _stats;

    public StatsApiController(IErrorStatsService stats) => _stats = stats;

    /// <summary>Chart series, breakdowns and the current anomaly verdict.</summary>
    [HttpGet("overview")]
    [ProducesResponseType<OverviewResponseModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview(
        CancellationToken cancellationToken,
        int days = 30,
        string? host = null)
    {
        OverviewResponseModel overview = await _stats.GetOverviewAsync(
            Math.Clamp(days, 1, MaxDays),
            NormaliseHost(host),
            cancellationToken);

        return Ok(overview);
    }

    /// <summary>Hosts that have reported at least once, for the dashboard's host filter.</summary>
    [HttpGet("hosts")]
    [ProducesResponseType<IEnumerable<string>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Hosts(CancellationToken cancellationToken) =>
        Ok(await _stats.GetHostsAsync(cancellationToken));

    /// <summary>URLs visitors are hitting errors on.</summary>
    [HttpGet("pages")]
    [ProducesResponseType<PagedViewModel<OffendingPageResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Pages(
        CancellationToken cancellationToken,
        int days = 30,
        string? host = null,
        string? type = null,
        int? statusCode = null,
        int skip = 0,
        int take = 50,
        PageSortField orderBy = PageSortField.Count,
        SortDirection direction = SortDirection.Descending)
    {
        DateTime now = DateTime.UtcNow;

        PagedViewModel<OffendingPageResponseModel> pages = await _stats.GetOffendingPagesAsync(
            now.Date.AddDays(-(Math.Clamp(days, 1, MaxDays) - 1)),
            now,
            NormaliseHost(host),
            NormaliseHost(type),
            statusCode,
            ClampSkip(skip),
            ClampTake(take),
            orderBy,
            direction,
            cancellationToken);

        return Ok(pages);
    }

    /// <summary>
    ///     Which pages still link to a broken URL.
    /// </summary>
    /// <remarks>
    ///     Drawn from raw reports rather than the aggregates, so the window it can actually cover is
    ///     capped by the raw retention setting; the response says what that turned out to be.
    /// </remarks>
    [HttpGet("referrers")]
    [ProducesResponseType<ReferrersResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Referrers(
        CancellationToken cancellationToken,
        string host,
        string path,
        int days = 30,
        int take = 20)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Both host and path are required.");
        }

        ReferrersResponseModel referrers = await _stats.GetReferrersAsync(
            host,
            path,
            Math.Clamp(days, 1, MaxDays),
            Math.Clamp(take, 1, 100),
            cancellationToken);

        return Ok(referrers);
    }

    /// <summary>
    ///     Raw reports as received, newest first. Exists so somebody setting the package up can confirm
    ///     the browser is actually reaching the collector, before the hourly job has rolled anything up.
    /// </summary>
    [HttpGet("reports")]
    [ProducesResponseType<PagedViewModel<RawReportResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reports(
        CancellationToken cancellationToken,
        string? host = null,
        int skip = 0,
        int take = 50)
    {
        PagedViewModel<RawReportResponseModel> reports = await _stats.GetRawReportsAsync(
            NormaliseHost(host),
            ClampSkip(skip),
            ClampTake(take),
            cancellationToken);

        return Ok(reports);
    }

    /// <summary>Treats an empty query-string value as "no filter" rather than "match the empty string".</summary>
    private static string? NormaliseHost(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
