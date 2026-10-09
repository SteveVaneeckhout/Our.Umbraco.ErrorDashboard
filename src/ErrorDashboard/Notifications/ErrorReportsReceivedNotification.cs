using Umbraco.Cms.Core.Notifications;

namespace Our.Umbraco.ErrorDashboard.Notifications;

/// <summary>
///     Published once per batch of network error reports, after the batch has been stored.
/// </summary>
/// <remarks>
///     The hook for sending reports somewhere else as well - a Grafana dashboard, a log pipeline, a
///     chat channel. Register a handler the usual Umbraco way:
///     <code>
///     builder.AddNotificationAsyncHandler&lt;ErrorReportsReceivedNotification, MyHandler&gt;();
///     </code>
///     <list type="bullet">
///         <item>
///             It carries a batch rather than one report, like Umbraco's own <c>ContentSavedNotification</c>:
///             reports are written up to 500 at a time, and most destinations accept batches too.
///         </item>
///         <item>
///             Every report is published exactly once, and only once it is in the database, so whatever
///             a handler counts matches what the dashboards show. A batch the database refused is not
///             published.
///         </item>
///         <item>
///             Handlers run on the background writer, never in a visitor's request - but the writer waits
///             for them. A slow handler holds up storage, and if the in-memory buffer fills meanwhile the
///             oldest reports are dropped. Keep handlers quick, or hand the work to your own queue.
///         </item>
///         <item>
///             An exception from a handler is logged and otherwise ignored: the reports are already
///             stored, and the writer carries on.
///         </item>
///         <item>
///             There is no HTTP request and no DI scope. Umbraco resolves handlers from the root
///             provider, so take <c>IServiceScopeFactory</c> rather than a scoped service.
///         </item>
///     </list>
/// </remarks>
public sealed class ErrorReportsReceivedNotification : INotification
{
    public ErrorReportsReceivedNotification(IReadOnlyList<ErrorReport> reports) => Reports = reports;

    /// <summary>The batch, in the order the collector accepted it. Never empty.</summary>
    public IReadOnlyList<ErrorReport> Reports { get; }
}
