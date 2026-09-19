using Our.Umbraco.ErrorDashboard.Persistence.Dtos;
using Our.Umbraco.ErrorDashboard.ViewModels;
using Umbraco.Cms.Core.Mail;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence.SqlSyntax;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Our.Umbraco.ErrorDashboard.Services;

/// <inheritdoc />
public sealed class SubscriptionService : ISubscriptionService
{
    private readonly IEmailSender _emailSender;
    private readonly IScopeProvider _scopeProvider;
    private readonly IUserService _userService;

    public SubscriptionService(
        IScopeProvider scopeProvider,
        IUserService userService,
        IEmailSender emailSender)
    {
        _scopeProvider = scopeProvider;
        _userService = userService;
        _emailSender = emailSender;
    }

    /// <inheritdoc />
    public Task<SubscriptionResponseModel> GetForUserAsync(Guid userKey, CancellationToken cancellationToken = default)
    {
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        SubscriptionDto? row = scope.Database.SingleOrDefault<SubscriptionDto>(
            $"WHERE {scope.SqlContext.SqlSyntax.GetQuotedColumnName("userKey")} = @0", userKey);

        return Task.FromResult(ToModel(row));
    }

    /// <inheritdoc />
    public Task<SubscriptionResponseModel> SetForUserAsync(
        Guid userKey,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        using IScope scope = _scopeProvider.CreateScope();
        string userKeyColumn = scope.SqlContext.SqlSyntax.GetQuotedColumnName("userKey");

        SubscriptionDto? row = scope.Database.SingleOrDefault<SubscriptionDto>($"WHERE {userKeyColumn} = @0", userKey);

        if (row is null)
        {
            row = new SubscriptionDto
            {
                UserKey = userKey,
                Enabled = enabled,
                CreatedUtc = DateTime.UtcNow,
            };

            // The primary key is the user's own Guid rather than an identity value, so NPoco has to be
            // told not to expect one back.
            scope.Database.Insert(SubscriptionDto.TableName_, nameof(SubscriptionDto.UserKey), false, row);
        }
        else
        {
            row.Enabled = enabled;
            scope.Database.Update(row);
        }

        scope.Complete();

        return Task.FromResult(ToModel(row));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubscriberResponseModel>> GetSubscribersAsync(
        CancellationToken cancellationToken = default)
    {
        List<SubscriptionDto> rows;

        using (IScope scope = _scopeProvider.CreateScope(autoComplete: true))
        {
            rows = scope.Database.Fetch<SubscriptionDto>(
                $"WHERE {scope.SqlContext.SqlSyntax.GetQuotedColumnName("enabled")} = @0", true);
        }

        if (rows.Count == 0)
        {
            return [];
        }

        // GetAsync(keys) rather than GetAllAsync: the latter needs a performing user to permission-filter
        // against, and there is no ambient user when this runs from the background job.
        IEnumerable<IUser> users = await _userService.GetAsync(rows.Select(row => row.UserKey));

        Dictionary<Guid, SubscriptionDto> byKey = rows.ToDictionary(row => row.UserKey);

        return
        [
            .. users
                // A disabled account with a stale subscription should not keep receiving mail, and an
                // address-less user cannot receive it at all.
                .Where(user => user.IsApproved && string.IsNullOrWhiteSpace(user.Email) is false)
                .Select(user => new SubscriberResponseModel
                {
                    UserKey = user.Key,
                    Name = user.Name ?? user.Username,
                    Email = user.Email,
                    Language = user.Language,
                    LastNotifiedUtc = byKey.TryGetValue(user.Key, out SubscriptionDto? row) ? row.LastNotifiedUtc : null,
                })
                .OrderBy(subscriber => subscriber.Name),
        ];
    }

    /// <inheritdoc />
    public Task MarkNotifiedAsync(
        IEnumerable<Guid> userKeys,
        DateTime notifiedUtc,
        CancellationToken cancellationToken = default)
    {
        Guid[] keys = [.. userKeys];
        if (keys.Length == 0)
        {
            return Task.CompletedTask;
        }

        using IScope scope = _scopeProvider.CreateScope();
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        scope.Database.Execute(
            $"""
             UPDATE {syntax.GetQuotedTableName(SubscriptionDto.TableName_)}
             SET {syntax.GetQuotedColumnName("lastNotifiedUtc")} = @0
             WHERE {syntax.GetQuotedColumnName("userKey")} IN (@1)
             """,
            notifiedUtc,
            keys);

        scope.Complete();

        return Task.CompletedTask;
    }

    private SubscriptionResponseModel ToModel(SubscriptionDto? row)
    {
        // Umbraco's EmailSender silently does nothing when neither an SMTP host nor a pickup directory
        // is configured - no exception, just a debug log - so without this check a user could switch
        // alerts on and never learn that nothing would ever be sent.
        bool canSend = _emailSender.CanSendRequiredEmail();

        return new SubscriptionResponseModel
        {
            Enabled = row?.Enabled ?? false,
            CanReceiveEmail = canSend,
            LastNotifiedUtc = row?.LastNotifiedUtc,
        };
    }
}
