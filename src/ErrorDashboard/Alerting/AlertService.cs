using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;
using Our.Umbraco.ErrorDashboard.Resources;
using Our.Umbraco.ErrorDashboard.Services;
using Our.Umbraco.ErrorDashboard.ViewModels;
using Umbraco.Cms.Api.Common.ViewModels.Pagination;
using Umbraco.Cms.Core.Mail;
using Umbraco.Cms.Core.Models.Email;
using Umbraco.Cms.Infrastructure.Persistence.SqlSyntax;
using Umbraco.Cms.Infrastructure.Scoping;
using UmbConstants = Umbraco.Cms.Core.Constants;

namespace Our.Umbraco.ErrorDashboard.Alerting;

/// <inheritdoc />
public sealed class AlertService : IAlertService
{
    /// <summary>Worst paths named in the email. Enough to point at a cause, short enough to read.</summary>
    private const int TopPathsInEmail = 10;

    /// <summary>Window the observation is taken over. A rolling day, compared against whole past days.</summary>
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromHours(24);

    private readonly IEmailSender _emailSender;
    private readonly ILogger<AlertService> _logger;
    private readonly IOptionsMonitor<ErrorDashboardOptions> _options;
    private readonly IScopeProvider _scopeProvider;
    private readonly IErrorStatsService _stats;
    private readonly ISubscriptionService _subscriptions;

    public AlertService(
        IErrorStatsService stats,
        ISubscriptionService subscriptions,
        IEmailSender emailSender,
        IScopeProvider scopeProvider,
        IOptionsMonitor<ErrorDashboardOptions> options,
        ILogger<AlertService> logger)
    {
        _stats = stats;
        _subscriptions = subscriptions;
        _emailSender = emailSender;
        _scopeProvider = scopeProvider;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        ErrorDashboardOptions options = _options.CurrentValue;

        if (options.Alerts.Enabled is false)
        {
            return false;
        }

        IReadOnlyList<string> hosts = await _stats.GetHostsAsync(cancellationToken);
        if (hosts.Count == 0)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        bool raised = false;

        // Each host is judged on its own history. A quiet staging hostname and a busy public one share
        // these tables but have nothing to say about each other's normal.
        foreach (string host in hosts)
        {
            if (await EvaluateHostAsync(host, now, options, cancellationToken))
            {
                raised = true;
            }
        }

        return raised;
    }

    private async Task<bool> EvaluateHostAsync(
        string host,
        DateTime now,
        ErrorDashboardOptions options,
        CancellationToken cancellationToken)
    {
        DateTime windowStart = now - ObservationWindow;

        long observed = await _stats.GetWindowCountAsync(host, windowStart, now, cancellationToken);

        // The baseline is whole days ending at midnight, so it overlaps the first part of the rolling
        // window: a spike that began yesterday afternoon inflates one baseline day as well as the
        // observation. That is tolerable precisely because the test uses a median rather than a mean -
        // one contaminated day out of fourteen barely moves it, where an average would be dragged far
        // enough to hide the very spike it is meant to catch.
        IReadOnlyList<double> baseline = await _stats.GetDailyBaselineAsync(
            host, now.Date, options.Alerts.BaselineDays, cancellationToken);

        AnomalyResult result = AnomalyDetector.Evaluate(
            observed, baseline, ErrorStatsService.ToThresholds(options.Alerts));

        if (result.IsAnomalous is false)
        {
            _logger.LogDebug("No alert for {Host}: {Explanation}", host, result.ToLogString());
            return false;
        }

        if (IsInCooldown(host, now, options.Alerts.CooldownHours))
        {
            _logger.LogInformation(
                "{Host} is still anomalous ({Explanation}) but an alert was already sent within the last {Hours}h.",
                host,
                result.ToLogString(),
                options.Alerts.CooldownHours);
            return false;
        }

        IReadOnlyList<string> topPaths = await _stats.GetTopPathsAsync(
            host, windowStart, now, TopPathsInEmail, cancellationToken);

        IReadOnlyList<SubscriberResponseModel> subscribers = await _subscriptions.GetSubscribersAsync(cancellationToken);

        int sent = await SendAsync(host, result, topPaths, subscribers, cancellationToken);

        RecordAlert(host, windowStart, now, result, topPaths, sent);

        if (sent > 0)
        {
            await _subscriptions.MarkNotifiedAsync(subscribers.Select(s => s.UserKey), now, cancellationToken);
        }

        return true;
    }

    private async Task<int> SendAsync(
        string host,
        AnomalyResult result,
        IReadOnlyList<string> topPaths,
        IReadOnlyList<SubscriberResponseModel> subscribers,
        CancellationToken cancellationToken)
    {
        if (subscribers.Count == 0)
        {
            _logger.LogInformation("{Host} is anomalous but nobody is subscribed to alerts.", host);
            return 0;
        }

        // EmailSender swallows the send when no SMTP host and no pickup directory are configured, so
        // without this the alert would vanish with nothing but a debug line to show for it.
        if (_emailSender.CanSendRequiredEmail() is false)
        {
            _logger.LogWarning(
                "{Host} is anomalous and {Count} user(s) are subscribed, but Umbraco has no SMTP configuration "
                + "so no email can be sent. Configure Umbraco:CMS:Global:Smtp.",
                host,
                subscribers.Count);
            return 0;
        }

        // One message per language rather than one per person: subscribers who share a language share
        // an identical email, and a single send keeps them on one recipient list the way it always was.
        // Grouping on the resolved culture, not the raw string, folds "nl" and "nl-NL" together - they
        // resolve to the same satellite assembly and would otherwise produce two identical mails.
        IEnumerable<IGrouping<CultureInfo, SubscriberResponseModel>> byCulture = subscribers
            .GroupBy(subscriber => AlertEmailText.CultureFor(subscriber.Language));

        var sent = 0;

        foreach (IGrouping<CultureInfo, SubscriberResponseModel> group in byCulture)
        {
            CultureInfo culture = group.Key;

            var message = new EmailMessage(
                from: null,
                to: [.. group.Select(subscriber => subscriber.Email)],
                cc: null,
                bcc: null,
                replyTo: null,
                subject: AlertEmailText.Get(
                    "Subject",
                    culture,
                    host,
                    result.Observed.ToString("0", culture)),
                body: BuildBody(host, result, topPaths, culture),
                isBodyHtml: true,
                attachments: null);

            try
            {
                await _emailSender.SendAsync(message, UmbConstants.Web.EmailTypes.Notification);
            }
            catch (Exception ex)
            {
                // A failed send must not stop the alert being recorded - the dashboard history is the
                // fallback channel, and losing it too would leave no trace that anything happened.
                // Nor should one language's failure cost the others their alert.
                _logger.LogError(ex, "Failed to send the {Culture} error alert for {Host}.", culture.Name, host);
                continue;
            }

            sent += group.Count();
        }

        _logger.LogInformation("Sent an error alert for {Host} to {Count} subscriber(s).", host, sent);
        return sent;
    }

    /// <param name="culture">
    ///     The recipients' language. Every number is formatted against it too - a Dutch reader expects
    ///     4,7 where an English one expects 4.7, and getting that wrong in a number-heavy email makes
    ///     it read as broken rather than as translated.
    /// </param>
    private static string BuildBody(
        string host,
        AnomalyResult result,
        IReadOnlyList<string> topPaths,
        CultureInfo culture)
    {
        var body = new StringBuilder();

        body.Append(AlertEmailText.Get("Intro", culture, WebUtility.HtmlEncode(host)));

        body.Append("<ul>")
            .Append(AlertEmailText.Get("Last24Hours", culture, result.Observed.ToString("0", culture)))
            .Append(AlertEmailText.Get("Usual", culture, result.Median.ToString("0.##", culture)))
            .Append(AlertEmailText.Get(
                "Severity",
                culture,
                result.Score.ToString("0.#", culture),
                result.BaselineDays))
            .Append("</ul>");

        if (topPaths.Count > 0)
        {
            body.Append(AlertEmailText.Get("WorstUrls", culture)).Append("<ol>");
            foreach (string path in topPaths)
            {
                body.Append("<li>").Append(WebUtility.HtmlEncode(path)).Append("</li>");
            }

            body.Append("</ol>");
        }

        body.Append(AlertEmailText.Get("Outro", culture));

        // Nobody should read these numbers as a complete picture, and the email is the one place the
        // caveat cannot be designed into the layout.
        body.Append("<p style=\"color:#666;font-size:12px\">")
            .Append(AlertEmailText.Get("CoverageNote", culture))
            .Append("</p>");

        return body.ToString();
    }

    private bool IsInCooldown(string host, DateTime now, int cooldownHours)
    {
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        int recent = scope.Database.ExecuteScalar<int>(
            $"""
             SELECT COUNT(*) FROM {syntax.GetQuotedTableName(AlertDto.TableName_)}
             WHERE {syntax.GetQuotedColumnName("host")} = @0
               AND {syntax.GetQuotedColumnName("raisedUtc")} >= @1
             """,
            host,
            now.AddHours(-cooldownHours));

        return recent > 0;
    }

    private void RecordAlert(
        string host,
        DateTime windowStart,
        DateTime now,
        AnomalyResult result,
        IReadOnlyList<string> topPaths,
        int recipientCount)
    {
        using IScope scope = _scopeProvider.CreateScope();

        scope.Database.Insert(new AlertDto
        {
            RaisedUtc = now,
            WindowStartUtc = windowStart,
            Host = host,
            Observed = (long)result.Observed,
            Median = result.Median,
            Scale = result.Scale,
            Score = result.Score,
            BaselineDays = result.BaselineDays,
            RecipientCount = recipientCount,
            TopPathsJson = JsonSerializer.Serialize(topPaths),
        });

        scope.Complete();
    }

    /// <inheritdoc />
    public Task<PagedViewModel<AlertResponseModel>> GetHistoryAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;
        string table = syntax.GetQuotedTableName(AlertDto.TableName_);

        int total = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");
        if (total == 0)
        {
            return Task.FromResult(PagedViewModel<AlertResponseModel>.Empty());
        }

        // SkipTake so NPoco writes the provider's own paging keywords; the total above is already
        // counted by hand, so Page<T> would only add a second, redundant count query.
        List<AlertDto> rows = scope.Database.SkipTake<AlertDto>(
            skip,
            take,
            $"""
             SELECT * FROM {table}
             ORDER BY {syntax.GetQuotedColumnName("raisedUtc")} DESC
             """);

        return Task.FromResult(new PagedViewModel<AlertResponseModel>
        {
            Total = total,
            Items = rows.Select(row => new AlertResponseModel
            {
                Id = row.Id,
                RaisedUtc = row.RaisedUtc,
                WindowStartUtc = row.WindowStartUtc,
                Host = row.Host,
                Observed = row.Observed,
                Median = row.Median,
                Score = row.Score,
                RecipientCount = row.RecipientCount,
                TopPaths = DeserialiseTopPaths(row.TopPathsJson),
            }).ToArray(),
        });
    }

    private static IEnumerable<string> DeserialiseTopPaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            // A history row with unreadable detail is still worth showing for its numbers.
            return [];
        }
    }
}
