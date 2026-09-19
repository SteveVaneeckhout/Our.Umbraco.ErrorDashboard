using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Our.Umbraco.ErrorDashboard.Alerting;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Jobs;
using Our.Umbraco.ErrorDashboard.Reporting;
using Our.Umbraco.ErrorDashboard.Services;
using Our.Umbraco.ErrorDashboard.ViewModels;
using Umbraco.Cms.Api.Common.ViewModels.Pagination;
using Umbraco.Cms.Core.Mail;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace Our.Umbraco.ErrorDashboard.Controllers;

/// <summary>Alert history, subscriptions and the settings the dashboard explains itself with.</summary>
[ApiVersion("1.0")]
[ApiExplorerSettings(GroupName = "ErrorDashboard")]
public class AlertsApiController : ErrorDashboardApiControllerBase
{
    private readonly IAlertService _alerts;
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly IEmailSender _emailSender;
    private readonly IReportHostValidator _hostValidator;
    private readonly ErrorDashboardAggregationJob _job;
    private readonly IOptionsMonitor<ErrorDashboardOptions> _options;
    private readonly ISubscriptionService _subscriptions;

    public AlertsApiController(
        IAlertService alerts,
        ISubscriptionService subscriptions,
        ErrorDashboardAggregationJob job,
        IEmailSender emailSender,
        IReportHostValidator hostValidator,
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        IOptionsMonitor<ErrorDashboardOptions> options)
    {
        _alerts = alerts;
        _subscriptions = subscriptions;
        _job = job;
        _emailSender = emailSender;
        _hostValidator = hostValidator;
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _options = options;
    }

    /// <summary>Past alerts, newest first.</summary>
    [HttpGet("alerts")]
    [ProducesResponseType<PagedViewModel<AlertResponseModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Alerts(CancellationToken cancellationToken, int skip = 0, int take = 50) =>
        Ok(await _alerts.GetHistoryAsync(ClampSkip(skip), ClampTake(take), cancellationToken));

    /// <summary>The calling user's own alert subscription.</summary>
    [HttpGet("subscription")]
    [ProducesResponseType<SubscriptionResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Subscription(CancellationToken cancellationToken)
    {
        Guid? userKey = CurrentUserKey();
        if (userKey is null)
        {
            return BadRequest("No backoffice user is associated with this request.");
        }

        return Ok(await _subscriptions.GetForUserAsync(userKey.Value, cancellationToken));
    }

    /// <summary>
    ///     Turns the calling user's own subscription on or off. Deliberately self-service only - one user
    ///     cannot sign another up for email they did not ask for.
    /// </summary>
    [HttpPut("subscription")]
    [ProducesResponseType<SubscriptionResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSubscription(
        UpdateSubscriptionRequestModel model,
        CancellationToken cancellationToken)
    {
        Guid? userKey = CurrentUserKey();
        if (userKey is null)
        {
            return BadRequest("No backoffice user is associated with this request.");
        }

        return Ok(await _subscriptions.SetForUserAsync(userKey.Value, model.Enabled, cancellationToken));
    }

    /// <summary>Everyone currently subscribed. Who else gets paged is administrative information.</summary>
    [HttpGet("subscribers")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType<IEnumerable<SubscriberResponseModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Subscribers(CancellationToken cancellationToken) =>
        Ok(await _subscriptions.GetSubscribersAsync(cancellationToken));

    /// <summary>Effective configuration, so the dashboard can state its own thresholds rather than imply them.</summary>
    [HttpGet("settings")]
    [ProducesResponseType<SettingsResponseModel>(StatusCodes.Status200OK)]
    public IActionResult Settings()
    {
        ErrorDashboardOptions options = _options.CurrentValue;

        return Ok(new SettingsResponseModel
        {
            Enabled = options.Enabled,
            ReportPath = options.ReportPath,
            SuccessFraction = options.SuccessFraction,
            FailureFraction = options.FailureFraction,
            RawRetentionDays = options.RawRetentionDays,
            RateLimitPerMinute = options.RateLimitPerMinute,
            AcceptedHosts = _hostValidator.KnownHosts,
            AlertsEnabled = options.Alerts.Enabled,
            BaselineDays = options.Alerts.BaselineDays,
            MinBaselineDays = options.Alerts.MinBaselineDays,
            ScoreThreshold = options.Alerts.ScoreThreshold,
            MinObserved = options.Alerts.MinObserved,
            MinRatio = options.Alerts.MinRatio,
            CooldownHours = options.Alerts.CooldownHours,
            CanSendEmail = _emailSender.CanSendRequiredEmail(),
        });
    }

    /// <summary>
    ///     Runs the hourly job immediately.
    /// </summary>
    /// <remarks>
    ///     Admin-only because it can send email. Every step is idempotent, so pressing it twice does
    ///     nothing the cooldown has not already prevented.
    /// </remarks>
    [HttpPost("recompute")]
    [Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
    [ProducesResponseType<RecomputeResponseModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Recompute(CancellationToken cancellationToken)
    {
        (int aggregated, int pruned, bool alertRaised) = await _job.RunOnceAsync(cancellationToken);

        return Ok(new RecomputeResponseModel
        {
            RowsAggregated = aggregated,
            RowsPruned = pruned,
            AlertRaised = alertRaised,
        });
    }

    private Guid? CurrentUserKey()
    {
        IUser? currentUser = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        return currentUser?.Key;
    }
}
