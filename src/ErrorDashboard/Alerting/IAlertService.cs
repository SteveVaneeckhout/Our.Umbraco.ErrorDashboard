using Our.Umbraco.ErrorDashboard.ViewModels;
using Umbraco.Cms.Api.Common.ViewModels.Pagination;

namespace Our.Umbraco.ErrorDashboard.Alerting;

/// <summary>Decides whether the current error volume warrants waking somebody up, and does the waking.</summary>
public interface IAlertService
{
    /// <summary>
    ///     Evaluates every reporting host and emails subscribers about any that look abnormal.
    /// </summary>
    /// <returns>True when at least one alert was raised.</returns>
    Task<bool> EvaluateAsync(CancellationToken cancellationToken = default);

    /// <summary>Past alerts, newest first.</summary>
    Task<PagedViewModel<AlertResponseModel>> GetHistoryAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
