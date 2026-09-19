using Our.Umbraco.ErrorDashboard.ViewModels;

namespace Our.Umbraco.ErrorDashboard.Services;

/// <summary>Who has asked to be emailed when this site's error rate goes abnormal.</summary>
public interface ISubscriptionService
{
    /// <summary>Reads one user's own subscription, including whether email could actually reach them.</summary>
    Task<SubscriptionResponseModel> GetForUserAsync(Guid userKey, CancellationToken cancellationToken = default);

    /// <summary>Turns one user's subscription on or off.</summary>
    Task<SubscriptionResponseModel> SetForUserAsync(Guid userKey, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every subscribed user who could actually be emailed - approved, with an address. Used both by
    ///     the admin list and by the alert service when deciding who to write to.
    /// </summary>
    Task<IReadOnlyList<SubscriberResponseModel>> GetSubscribersAsync(CancellationToken cancellationToken = default);

    /// <summary>Stamps the given users as notified, so the dashboard can show when they last heard from us.</summary>
    Task MarkNotifiedAsync(IEnumerable<Guid> userKeys, DateTime notifiedUtc, CancellationToken cancellationToken = default);
}
