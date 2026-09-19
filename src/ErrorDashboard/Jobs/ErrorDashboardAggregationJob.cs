using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Our.Umbraco.ErrorDashboard.Alerting;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Services;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace Our.Umbraco.ErrorDashboard.Jobs;

/// <summary>
///     Hourly: folds new raw reports into the aggregate tables, evaluates the alert test, and prunes
///     whatever has aged out.
/// </summary>
/// <remarks>
///     <para>
///         Derives from <see cref="RecurringBackgroundJobBase" /> rather than implementing
///         <c>IRecurringBackgroundJob</c> directly - the interface would force an implementation of the
///         obsolete parameterless <c>RunJobAsync()</c> and a hand-rolled <c>PeriodChanged</c> event.
///         The base already defaults <c>ServerRoles</c> to Single plus SchedulingPublisher, which is
///         exactly what this needs, and the hosting service handles MainDom and runtime-level gating.
///     </para>
///     <para>
///         Registered as a singleton by <c>AddRecurringBackgroundJob</c>, so every dependency here must
///         be a singleton too.
///     </para>
/// </remarks>
public sealed class ErrorDashboardAggregationJob : RecurringBackgroundJobBase
{
    /// <summary>
    ///     Hourly rather than daily. The statistics are the same either way - a rolling 24h window
    ///     against whole past days - but checking every hour means a site that breaks at 09:00 is
    ///     reported before lunch instead of the following morning.
    /// </summary>
    private static readonly TimeSpan RunPeriod = TimeSpan.FromHours(1);

    private readonly IAlertService _alerts;
    private readonly ILogger<ErrorDashboardAggregationJob> _logger;
    private readonly IOptionsMonitor<ErrorDashboardOptions> _options;
    private readonly IErrorStatsService _stats;

    public ErrorDashboardAggregationJob(
        IErrorStatsService stats,
        IAlertService alerts,
        IOptionsMonitor<ErrorDashboardOptions> options,
        ILogger<ErrorDashboardAggregationJob> logger)
        : base(RunPeriod)
    {
        _stats = stats;
        _alerts = alerts;
        _options = options;
        _logger = logger;
    }

    public override async Task RunJobAsync(CancellationToken cancellationToken)
    {
        if (_options.CurrentValue.Enabled is false)
        {
            return;
        }

        await RunOnceAsync(cancellationToken);
    }

    /// <summary>
    ///     The job's actual work, exposed so the dashboard's "Recompute now" button can run it without
    ///     waiting an hour. Every step is idempotent, so running it twice in a row is harmless.
    /// </summary>
    public async Task<(int Aggregated, int Pruned, bool AlertRaised)> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        int aggregated = await _stats.AggregateAsync(cancellationToken);

        // Alerting reads the aggregate tables, so it has to follow aggregation within the same run or
        // it would always be judging an hour-old picture.
        bool alertRaised = await _alerts.EvaluateAsync(cancellationToken);

        // Pruning last, so a report is never deleted before it has been counted.
        int pruned = await _stats.PruneAsync(cancellationToken);

        _logger.LogDebug(
            "Error dashboard run complete: {Aggregated} aggregated, {Pruned} pruned, alert raised: {AlertRaised}.",
            aggregated,
            pruned,
            alertRaised);

        return (aggregated, pruned, alertRaised);
    }
}
