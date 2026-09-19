using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Our.Umbraco.ErrorDashboard.Alerting;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;
using Our.Umbraco.ErrorDashboard.Reporting;
using Our.Umbraco.ErrorDashboard.ViewModels;
using Umbraco.Cms.Api.Common.ViewModels.Pagination;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence.SqlSyntax;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Our.Umbraco.ErrorDashboard.Services;

/// <inheritdoc />
public sealed class ErrorStatsService : IErrorStatsService
{
    /// <summary>Key under which the aggregation watermark lives in <c>umbracoKeyValue</c>.</summary>
    private const string WatermarkKey = "ErrorDashboard.AggregationWatermark";

    /// <summary>Raw rows folded in per run. Caps the memory a long backlog can cost.</summary>
    private const int AggregationBatchSize = 20_000;

    /// <summary>The NEL type carried by sampled successes, which are a denominator rather than an error.</summary>
    private const string OkType = "ok";

    /// <summary>
    ///     Rows younger than this are left for the next run.
    /// </summary>
    /// <remarks>
    ///     The watermark is an identity value, and identity is assigned at insert but only becomes
    ///     visible at commit - so in a load-balanced setup a row with a lower id can appear after one
    ///     with a higher id has already been passed. Ignoring the most recent few seconds closes that
    ///     window, since the writer flushes every five.
    /// </remarks>
    private static readonly TimeSpan CommitSettleTime = TimeSpan.FromMinutes(1);

    private readonly IDocumentUrlService _documentUrlService;
    private readonly IKeyValueService _keyValueService;
    private readonly ILogger<ErrorStatsService> _logger;
    private readonly IOptionsMonitor<ErrorDashboardOptions> _options;
    private readonly IScopeProvider _scopeProvider;

    public ErrorStatsService(
        IScopeProvider scopeProvider,
        IKeyValueService keyValueService,
        IDocumentUrlService documentUrlService,
        IOptionsMonitor<ErrorDashboardOptions> options,
        ILogger<ErrorStatsService> logger)
    {
        _scopeProvider = scopeProvider;
        _keyValueService = keyValueService;
        _documentUrlService = documentUrlService;
        _options = options;
        _logger = logger;
    }

    // ---------------------------------------------------------------- aggregation

    /// <inheritdoc />
    public Task<int> AggregateAsync(CancellationToken cancellationToken = default)
    {
        ErrorDashboardOptions options = _options.CurrentValue;
        long watermark = ReadWatermark();
        DateTime cutoff = DateTime.UtcNow - CommitSettleTime;

        using IScope scope = _scopeProvider.CreateScope();
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;
        string reports = syntax.GetQuotedTableName(NelReportDto.TableName_);

        // SkipTake rather than a TOP/LIMIT clause of our own: NPoco renders the paging keywords for
        // whichever provider is configured, and they are the one part of these statements that
        // GetQuotedTableName/GetQuotedColumnName cannot make portable.
        List<NelReportDto> rows = scope.Database.SkipTake<NelReportDto>(
            0,
            AggregationBatchSize,
            $"""
             SELECT *
             FROM {reports}
             WHERE {syntax.GetQuotedColumnName("id")} > @0
               AND {syntax.GetQuotedColumnName("receivedUtc")} <= @1
             ORDER BY {syntax.GetQuotedColumnName("id")}
             """,
            watermark,
            cutoff);

        if (rows.Count == 0)
        {
            scope.Complete();
            return Task.FromResult(0);
        }

        ApplyHourlyDeltas(scope, syntax, rows);
        ApplyUrlDailyDeltas(scope, syntax, rows, options.TopPathsPerDay);

        long newWatermark = rows.Max(row => row.Id);
        scope.Complete();

        WriteWatermark(newWatermark);
        _logger.LogInformation("Aggregated {Count} network error report(s) up to id {Watermark}.", rows.Count, newWatermark);

        return Task.FromResult(rows.Count);
    }

    /// <summary>
    ///     Folds the batch into the hourly table. Grouping happens in memory rather than SQL because the
    ///     batch is already loaded and hour truncation is one of the few things that differs materially
    ///     between database engines.
    /// </summary>
    private static void ApplyHourlyDeltas(IScope scope, ISqlSyntaxProvider syntax, List<NelReportDto> rows)
    {
        string table = syntax.GetQuotedTableName(StatHourlyDto.TableName_);
        string bucket = syntax.GetQuotedColumnName("bucketUtc");
        string host = syntax.GetQuotedColumnName("host");
        string type = syntax.GetQuotedColumnName("type");
        string status = syntax.GetQuotedColumnName("statusCode");
        string rawCount = syntax.GetQuotedColumnName("rawCount");
        string weighted = syntax.GetQuotedColumnName("weightedCount");
        string estimated = syntax.GetQuotedColumnName("estimatedRequests");

        var groups = rows
            .GroupBy(row => (
                Bucket: NelReportParser.ToUtcHour(row.OccurredUtc),
                row.Host,
                row.Type,
                Status: row.StatusCode ?? 0))
            .Select(group => new
            {
                group.Key,
                // Sampled successes are a traffic denominator, not an error, so they contribute to
                // estimatedRequests only and never to the counts the alert test reads.
                RawCount = group.LongCount(row => row.IsSuccess is false),
                Weighted = group.Where(row => row.IsSuccess is false).Sum(row => 1.0 / row.SamplingFraction),
                Estimated = group.Where(row => row.IsSuccess).Sum(row => 1.0 / row.SamplingFraction),
            });

        foreach (var group in groups)
        {
            int affected = scope.Database.Execute(
                $"""
                 UPDATE {table}
                 SET {rawCount} = {rawCount} + @0,
                     {weighted} = {weighted} + @1,
                     {estimated} = {estimated} + @2
                 WHERE {bucket} = @3 AND {host} = @4 AND {type} = @5 AND {status} = @6
                 """,
                group.RawCount,
                group.Weighted,
                group.Estimated,
                group.Key.Bucket,
                group.Key.Host,
                group.Key.Type,
                group.Key.Status);

            if (affected > 0)
            {
                continue;
            }

            scope.Database.Insert(new StatHourlyDto
            {
                BucketUtc = group.Key.Bucket,
                Host = group.Key.Host,
                Type = group.Key.Type,
                StatusCode = group.Key.Status,
                RawCount = group.RawCount,
                WeightedCount = group.Weighted,
                EstimatedRequests = group.Estimated,
            });
        }
    }

    /// <summary>
    ///     Folds the batch into the per-URL daily table, collapsing the long tail once a day has as many
    ///     distinct paths as it is allowed. Without the cap a site being scanned for nonexistent URLs
    ///     would add a row per probe and this table would dwarf the raw one it was meant to summarise.
    /// </summary>
    private void ApplyUrlDailyDeltas(IScope scope, ISqlSyntaxProvider syntax, List<NelReportDto> rows, int topPathsPerDay)
    {
        string table = syntax.GetQuotedTableName(StatUrlDailyDto.TableName_);
        string date = syntax.GetQuotedColumnName("date");
        string host = syntax.GetQuotedColumnName("host");
        string pathHash = syntax.GetQuotedColumnName("pathHash");
        string type = syntax.GetQuotedColumnName("type");
        string status = syntax.GetQuotedColumnName("statusCode");
        string rawCount = syntax.GetQuotedColumnName("rawCount");
        string weighted = syntax.GetQuotedColumnName("weightedCount");
        string lastSeen = syntax.GetQuotedColumnName("lastSeenUtc");

        // Successes carry no path worth showing - they exist to count traffic, not to be listed.
        var groups = rows
            .Where(row => row.IsSuccess is false)
            .GroupBy(row => (
                Date: NelReportParser.ToUtcDay(row.OccurredUtc),
                row.Host,
                row.Path,
                row.Type,
                Status: row.StatusCode ?? 0))
            .Select(group => new
            {
                group.Key,
                RawCount = group.LongCount(),
                Weighted = group.Sum(row => 1.0 / row.SamplingFraction),
                FirstSeen = group.Min(row => row.OccurredUtc),
                LastSeen = group.Max(row => row.OccurredUtc),
            })
            .OrderByDescending(group => group.RawCount)
            .ToList();

        // One count per (day, host) rather than per group, so the cap costs a handful of queries.
        Dictionary<(DateTime Date, string Host), int> distinctPaths = [];

        foreach (var group in groups)
        {
            (DateTime Date, string Host) dayKey = (group.Key.Date, group.Key.Host);

            if (distinctPaths.TryGetValue(dayKey, out int existing) is false)
            {
                existing = scope.Database.ExecuteScalar<int>(
                    $"""
                     SELECT COUNT(DISTINCT {pathHash})
                     FROM {table}
                     WHERE {date} = @0 AND {host} = @1
                     """,
                    dayKey.Date,
                    dayKey.Host);

                distinctPaths[dayKey] = existing;
            }

            string path = group.Key.Path;
            string hash = NelReportParser.Hash(path);

            int affected = scope.Database.Execute(
                $"""
                 UPDATE {table}
                 SET {rawCount} = {rawCount} + @0,
                     {weighted} = {weighted} + @1,
                     {lastSeen} = CASE WHEN {lastSeen} < @2 THEN @2 ELSE {lastSeen} END
                 WHERE {date} = @3 AND {host} = @4 AND {pathHash} = @5 AND {type} = @6 AND {status} = @7
                 """,
                group.RawCount,
                group.Weighted,
                group.LastSeen,
                group.Key.Date,
                group.Key.Host,
                hash,
                group.Key.Type,
                group.Key.Status);

            if (affected > 0)
            {
                continue;
            }

            // New path for this day. If the day is full, fold it into the catch-all row instead.
            if (distinctPaths[dayKey] >= topPathsPerDay)
            {
                path = StatUrlDailyDto.OtherPath;
                hash = NelReportParser.Hash(path);

                affected = scope.Database.Execute(
                    $"""
                     UPDATE {table}
                     SET {rawCount} = {rawCount} + @0,
                         {weighted} = {weighted} + @1,
                         {lastSeen} = CASE WHEN {lastSeen} < @2 THEN @2 ELSE {lastSeen} END
                     WHERE {date} = @3 AND {host} = @4 AND {pathHash} = @5 AND {type} = @6 AND {status} = @7
                     """,
                    group.RawCount,
                    group.Weighted,
                    group.LastSeen,
                    group.Key.Date,
                    group.Key.Host,
                    hash,
                    group.Key.Type,
                    group.Key.Status);

                if (affected > 0)
                {
                    continue;
                }
            }
            else
            {
                distinctPaths[dayKey] = distinctPaths[dayKey] + 1;
            }

            scope.Database.Insert(new StatUrlDailyDto
            {
                Date = group.Key.Date,
                Host = group.Key.Host,
                PathHash = hash,
                Path = path,
                Type = group.Key.Type,
                StatusCode = group.Key.Status,
                RawCount = group.RawCount,
                WeightedCount = group.Weighted,
                FirstSeenUtc = group.FirstSeen,
                LastSeenUtc = group.LastSeen,
            });
        }
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        ErrorDashboardOptions options = _options.CurrentValue;
        DateTime now = DateTime.UtcNow;

        using IScope scope = _scopeProvider.CreateScope();
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        // Pruned on receipt rather than occurrence, so a row is always kept for the full retention
        // period after it arrived - a late report cannot land in a window that has already been swept.
        int removed = scope.Database.Execute(
            $"""
             DELETE FROM {syntax.GetQuotedTableName(NelReportDto.TableName_)}
             WHERE {syntax.GetQuotedColumnName("receivedUtc")} < @0
             """,
            now.AddDays(-options.RawRetentionDays));

        removed += scope.Database.Execute(
            $"""
             DELETE FROM {syntax.GetQuotedTableName(StatUrlDailyDto.TableName_)}
             WHERE {syntax.GetQuotedColumnName("date")} < @0
             """,
            now.Date.AddDays(-options.UrlStatRetentionDays));

        scope.Complete();

        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Count} expired error dashboard row(s).", removed);
        }

        return Task.FromResult(removed);
    }

    // ---------------------------------------------------------------- reads

    /// <inheritdoc />
    public async Task<OverviewResponseModel> GetOverviewAsync(
        int days,
        string? host,
        CancellationToken cancellationToken = default)
    {
        ErrorDashboardOptions options = _options.CurrentValue;
        DateTime now = DateTime.UtcNow;
        DateTime from = now.Date.AddDays(-(days - 1));

        List<StatHourlyDto> buckets = FetchHourly(from, now.AddHours(1), host);
        List<StatHourlyDto> errors = [.. buckets.Where(IsErrorBucket)];

        // Days with no errors still need a point, or the chart silently compresses quiet periods and
        // a gap reads as "no data" rather than "nothing broke".
        Dictionary<DateTime, List<StatHourlyDto>> byDay = buckets
            .GroupBy(bucket => bucket.BucketUtc.Date)
            .ToDictionary(group => group.Key, group => group.ToList());

        List<DailyPointResponseModel> series = [];
        for (int offset = 0; offset < days; offset++)
        {
            DateTime day = from.AddDays(offset);
            byDay.TryGetValue(day, out List<StatHourlyDto>? dayBuckets);

            series.Add(new DailyPointResponseModel
            {
                Date = day,
                Count = dayBuckets?.Where(IsErrorBucket).Sum(bucket => bucket.RawCount) ?? 0,
                WeightedCount = dayBuckets?.Where(IsErrorBucket).Sum(bucket => bucket.WeightedCount) ?? 0,
                EstimatedRequests = dayBuckets?.Sum(bucket => bucket.EstimatedRequests) ?? 0,
            });
        }

        DateTime windowStart = now.AddHours(-24);
        long observed = errors
            .Where(bucket => bucket.BucketUtc >= NelReportParser.ToUtcHour(windowStart))
            .Sum(bucket => bucket.RawCount);

        // Judged against the same host filter the rest of the page is showing, so the verdict always
        // describes the numbers immediately above it. With no filter that is every host combined,
        // which is the right default view - alerting still evaluates each host separately, since a
        // quiet staging hostname has nothing to say about a busy public one's normal.
        IReadOnlyList<double> baseline = await GetDailyBaselineAsync(
            host, now.Date, options.Alerts.BaselineDays, cancellationToken);

        AnomalyResult result = AnomalyDetector.Evaluate(observed, baseline, ToThresholds(options.Alerts));
        AnomalyStateResponseModel anomaly = ToStateModel(result);

        return new OverviewResponseModel
        {
            Series = series,
            ByType = errors
                .GroupBy(bucket => bucket.Type)
                .Select(group => new ErrorTypeCountResponseModel
                {
                    Type = group.Key,
                    Count = group.Sum(bucket => bucket.RawCount),
                })
                .Where(entry => entry.Count > 0)
                .OrderByDescending(entry => entry.Count)
                .ToArray(),
            ByStatus = errors
                .Where(bucket => bucket.StatusCode > 0)
                .GroupBy(bucket => bucket.StatusCode)
                .Select(group => new StatusCountResponseModel
                {
                    StatusCode = group.Key,
                    Count = group.Sum(bucket => bucket.RawCount),
                })
                .Where(entry => entry.Count > 0)
                .OrderByDescending(entry => entry.Count)
                .ToArray(),
            ObservedLast24Hours = observed,
            Anomaly = anomaly,
            PendingRawReports = CountPendingRaw(),
            LastAggregatedUtc = ReadWatermarkTimestamp(),
            Hosts = await GetHostsAsync(cancellationToken),
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<double>> GetDailyBaselineAsync(
        string? host,
        DateTime beforeUtc,
        int days,
        CancellationToken cancellationToken = default)
    {
        DateTime end = beforeUtc.Date;
        DateTime start = end.AddDays(-days);

        List<StatHourlyDto> buckets = FetchHourly(start, end, host);

        Dictionary<DateTime, long> byDay = buckets
            .Where(IsErrorBucket)
            .GroupBy(bucket => bucket.BucketUtc.Date)
            .ToDictionary(group => group.Key, group => group.Sum(bucket => bucket.RawCount));

        // A day with no rows is a real zero, not missing data - the site was up and nothing broke - so
        // it belongs in the baseline. Dropping it would make the site look noisier than it is.
        List<double> baseline = [];
        for (int offset = 0; offset < days; offset++)
        {
            DateTime day = start.AddDays(offset);
            baseline.Add(byDay.TryGetValue(day, out long count) ? count : 0);
        }

        return Task.FromResult<IReadOnlyList<double>>(baseline);
    }

    /// <inheritdoc />
    public Task<long> GetWindowCountAsync(
        string? host,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        List<StatHourlyDto> buckets = FetchHourly(NelReportParser.ToUtcHour(fromUtc), toUtc, host);
        return Task.FromResult(buckets.Where(IsErrorBucket).Sum(bucket => bucket.RawCount));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetHostsAsync(CancellationToken cancellationToken = default)
    {
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        List<string> hosts = scope.Database.Fetch<string>(
            $"""
             SELECT DISTINCT {syntax.GetQuotedColumnName("host")}
             FROM {syntax.GetQuotedTableName(StatHourlyDto.TableName_)}
             ORDER BY {syntax.GetQuotedColumnName("host")}
             """);

        return Task.FromResult<IReadOnlyList<string>>(hosts);
    }

    /// <inheritdoc />
    public Task<PagedViewModel<OffendingPageResponseModel>> GetOffendingPagesAsync(
        DateTime fromUtc,
        DateTime toUtc,
        string? host,
        string? type,
        int? statusCode,
        int skip,
        int take,
        PageSortField sortField,
        SortDirection direction,
        CancellationToken cancellationToken = default)
    {
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        string table = syntax.GetQuotedTableName(StatUrlDailyDto.TableName_);
        string dateColumn = syntax.GetQuotedColumnName("date");
        string hostColumn = syntax.GetQuotedColumnName("host");
        string pathColumn = syntax.GetQuotedColumnName("path");
        string typeColumn = syntax.GetQuotedColumnName("type");
        string statusColumn = syntax.GetQuotedColumnName("statusCode");
        string rawCountColumn = syntax.GetQuotedColumnName("rawCount");
        string firstSeenColumn = syntax.GetQuotedColumnName("firstSeenUtc");
        string lastSeenColumn = syntax.GetQuotedColumnName("lastSeenUtc");

        List<object> args = [fromUtc.Date, toUtc.Date];
        var where = $"WHERE {dateColumn} >= @0 AND {dateColumn} <= @1";

        if (string.IsNullOrWhiteSpace(host) is false)
        {
            where += $" AND {hostColumn} = @{args.Count}";
            args.Add(host);
        }

        if (string.IsNullOrWhiteSpace(type) is false)
        {
            where += $" AND {typeColumn} = @{args.Count}";
            args.Add(type);
        }

        if (statusCode.HasValue)
        {
            where += $" AND {statusColumn} = @{args.Count}";
            args.Add(statusCode.Value);
        }

        var groupBy = $"GROUP BY {hostColumn}, {pathColumn}, {typeColumn}, {statusColumn}";

        int total = scope.Database.ExecuteScalar<int>(
            $"""
             SELECT COUNT(*) FROM (
                 SELECT {hostColumn} FROM {table} {where} {groupBy}
             ) grouped
             """,
            args.ToArray());

        if (total == 0)
        {
            return Task.FromResult(PagedViewModel<OffendingPageResponseModel>.Empty());
        }

        string orderColumn = sortField switch
        {
            PageSortField.LastSeen => "LastSeenUtc",
            PageSortField.Path => "Path",
            _ => "Count",
        };
        string orderDirection = direction == SortDirection.Ascending ? "ASC" : "DESC";

        // SkipTake, not Page: the total above counts *groups*, via the subquery, where NPoco's
        // generated count would count the rows going into the GROUP BY and overstate every page.
        List<OffendingPageRow> rows = scope.Database.SkipTake<OffendingPageRow>(
            skip,
            take,
            $"""
             SELECT {hostColumn} AS Host,
                    {pathColumn} AS Path,
                    {typeColumn} AS Type,
                    {statusColumn} AS StatusCode,
                    SUM({rawCountColumn}) AS Count,
                    MIN({firstSeenColumn}) AS FirstSeenUtc,
                    MAX({lastSeenColumn}) AS LastSeenUtc
             FROM {table}
             {where}
             {groupBy}
             ORDER BY {orderColumn} {orderDirection}
             """,
            args.ToArray());

        HashSet<string> withReferrers = FindUrlsWithReferrers(scope, rows, fromUtc, toUtc);

        return Task.FromResult(new PagedViewModel<OffendingPageResponseModel>
        {
            Total = total,
            Items = rows.Select(row =>
            {
                (Guid? documentKey, bool isUnpublished) = ResolveDocument(row.Host, row.Path);

                return new OffendingPageResponseModel
                {
                    HasReferrers = withReferrers.Contains(NelReportParser.Hash($"{row.Host}{row.Path}")),
                    Host = row.Host,
                    Path = row.Path,
                    Type = row.Type,
                    // Zero is the table's stand-in for "no status", since a null cannot take part in
                    // the unique key. Translate it back at the edge rather than showing an HTTP 0.
                    StatusCode = row.StatusCode == 0 ? null : row.StatusCode,
                    Count = row.Count,
                    FirstSeenUtc = row.FirstSeenUtc,
                    LastSeenUtc = row.LastSeenUtc,
                    DocumentKey = documentKey,
                    IsUnpublished = isUnpublished,
                };
            }).ToArray(),
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetTopPathsAsync(
        string host,
        DateTime fromUtc,
        DateTime toUtc,
        int take,
        CancellationToken cancellationToken = default)
    {
        PagedViewModel<OffendingPageResponseModel> pages = await GetOffendingPagesAsync(
            fromUtc, toUtc, host, type: null, statusCode: null, skip: 0, take: take,
            PageSortField.Count, SortDirection.Descending, cancellationToken);

        return [.. pages.Items.Select(page =>
            page.StatusCode.HasValue
                ? $"{page.Path} - {page.StatusCode} ({page.Count})"
                : $"{page.Path} - {page.Type} ({page.Count})")];
    }

    /// <summary>
    ///     Of these rows, which URLs something is known to link to - the hashes of the ones whose
    ///     referrer panel would have a referrer in it.
    /// </summary>
    /// <remarks>
    ///     One grouped query for the whole page of rows rather than one per row, and on the indexed
    ///     url hash rather than host and path. The window is deliberately the same one
    ///     <see cref="GetReferrersAsync" /> will use, so the disclosure arrow and the panel behind it
    ///     cannot disagree.
    ///     <para>
    ///         Two different things make a row unexpandable and both are caught here. The reports may
    ///         have been pruned - aggregates outlive them, so a row can carry counts long after the
    ///         raw rows behind it went - or every report may have arrived without a <c>Referer</c>
    ///         header, which the panel would only be able to render as "nothing links here".
    ///     </para>
    /// </remarks>
    private HashSet<string> FindUrlsWithReferrers(
        IScope scope,
        IReadOnlyCollection<OffendingPageRow> rows,
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var windowDays = Math.Max((toUtc.Date - fromUtc.Date).Days + 1, 1);
        DateTime from = DateTime.UtcNow.AddDays(-Math.Min(windowDays, _options.CurrentValue.RawRetentionDays));

        string[] hashes = [.. rows.Select(row => NelReportParser.Hash($"{row.Host}{row.Path}")).Distinct()];

        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;
        string hashColumn = syntax.GetQuotedColumnName("urlHash");
        string referrerColumn = syntax.GetQuotedColumnName("referrer");

        List<string> present = scope.Database.Fetch<string>(
            $"""
             SELECT DISTINCT {hashColumn}
             FROM {syntax.GetQuotedTableName(NelReportDto.TableName_)}
             WHERE {hashColumn} IN (@0)
               AND {syntax.GetQuotedColumnName("occurredUtc")} >= @1
               AND {syntax.GetQuotedColumnName("isSuccess")} = @2
               AND {referrerColumn} IS NOT NULL
               AND {referrerColumn} <> @3
             """,
            hashes,
            from,
            false,
            string.Empty);

        return [.. present];
    }

    /// <inheritdoc />
    public Task<ReferrersResponseModel> GetReferrersAsync(
        string host,
        string path,
        int days,
        int take,
        CancellationToken cancellationToken = default)
    {
        ErrorDashboardOptions options = _options.CurrentValue;

        // Referrers are not aggregated - a broken URL can be linked from anywhere, so the cardinality
        // is unbounded in a way the per-path totals are not. That makes this a raw-table query, and
        // caps how far back it can see at whatever the raw retention is.
        int availableDays = Math.Min(days, options.RawRetentionDays);
        DateTime from = DateTime.UtcNow.AddDays(-availableDays);

        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        List<ReferrerRow> rows = scope.Database.SkipTake<ReferrerRow>(
            0,
            take,
            $"""
             SELECT {syntax.GetQuotedColumnName("referrer")} AS Referrer,
                    COUNT(*) AS Count,
                    MAX({syntax.GetQuotedColumnName("occurredUtc")}) AS LastSeenUtc
             FROM {syntax.GetQuotedTableName(NelReportDto.TableName_)}
             WHERE {syntax.GetQuotedColumnName("host")} = @0
               AND {syntax.GetQuotedColumnName("path")} = @1
               AND {syntax.GetQuotedColumnName("occurredUtc")} >= @2
               AND {syntax.GetQuotedColumnName("isSuccess")} = @3
             GROUP BY {syntax.GetQuotedColumnName("referrer")}
             ORDER BY COUNT(*) DESC
             """,
            host,
            path,
            from,
            false);

        return Task.FromResult(new ReferrersResponseModel
        {
            AvailableDays = availableDays,
            Items = rows.Select(row => new ReferrerResponseModel
            {
                // Null means the browser sent no Referer header - a direct hit, a bookmark, or a
                // cross-origin navigation the referrer policy stripped. Worth showing as its own row
                // rather than dropping, because "nothing links to this" is itself the answer.
                Referrer = string.IsNullOrWhiteSpace(row.Referrer) ? null : row.Referrer,
                Count = row.Count,
                LastSeenUtc = row.LastSeenUtc,
            }).ToArray(),
        });
    }

    /// <inheritdoc />
    public Task<PagedViewModel<RawReportResponseModel>> GetRawReportsAsync(
        string? host,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        string table = syntax.GetQuotedTableName(NelReportDto.TableName_);
        string hostColumn = syntax.GetQuotedColumnName("host");

        List<object> args = [];
        var where = string.Empty;

        if (string.IsNullOrWhiteSpace(host) is false)
        {
            where = $"WHERE {hostColumn} = @0";
            args.Add(host);
        }

        int total = scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} {where}", args.ToArray());

        if (total == 0)
        {
            return Task.FromResult(PagedViewModel<RawReportResponseModel>.Empty());
        }

        List<NelReportDto> rows = scope.Database.SkipTake<NelReportDto>(
            skip,
            take,
            $"""
             SELECT * FROM {table}
             {where}
             ORDER BY {syntax.GetQuotedColumnName("id")} DESC
             """,
            args.ToArray());

        return Task.FromResult(new PagedViewModel<RawReportResponseModel>
        {
            Total = total,
            Items = rows.Select(row => new RawReportResponseModel
            {
                Id = row.Id,
                ReceivedUtc = row.ReceivedUtc,
                OccurredUtc = row.OccurredUtc,
                Url = row.Url,
                Type = row.Type,
                Phase = row.Phase,
                StatusCode = row.StatusCode,
                Method = row.Method,
                Protocol = row.Protocol,
                ServerIp = row.ServerIp,
                Referrer = row.Referrer,
                ElapsedTimeMs = row.ElapsedTimeMs,
            }).ToArray(),
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Maps configuration onto the detector's own threshold type, which knows nothing about Umbraco.</summary>
    public static AnomalyThresholds ToThresholds(AlertOptions options) =>
        new(options.MinBaselineDays, options.ScoreThreshold, options.MinObserved, options.MinRatio);

    /// <summary>Projects a detector verdict for the wire.</summary>
    public static AnomalyStateResponseModel ToStateModel(AnomalyResult result) =>
        new()
        {
            IsAnomalous = result.IsAnomalous,
            Observed = result.Observed,
            Median = result.Median,
            Scale = result.Scale,
            Score = result.Score,
            BaselineDays = result.BaselineDays,
            Explanation = result.Explanation,
            ExplanationArgs = result.ExplanationArgs,
        };

    /// <summary>
    ///     Finds the content node a reported path belongs to, so the dashboard can link straight to it.
    /// </summary>
    /// <remarks>
    ///     Published content is checked first, then drafts. The draft pass is the interesting one: a 404
    ///     on a path that resolves only as a draft means the page exists but is not live, which is a
    ///     different problem - and a far more fixable one - than a link to a page that never existed.
    ///     <para>
    ///         Resolution goes through <c>GetDocumentKeyByUri</c> rather than a route string so that
    ///         Umbraco applies its own domain and culture rules, which is the part that is easy to get
    ///         subtly wrong on a multi-site install.
    ///     </para>
    /// </remarks>
    private (Guid? DocumentKey, bool IsUnpublished) ResolveDocument(string host, string path)
    {
        if (Uri.TryCreate($"https://{host}{path}", UriKind.Absolute, out Uri? uri) is false)
        {
            return (null, false);
        }

        try
        {
            Guid? published = _documentUrlService.GetDocumentKeyByUri(uri, false);
            if (published.HasValue)
            {
                return (published, false);
            }

            Guid? draft = _documentUrlService.GetDocumentKeyByUri(uri, true);
            return draft.HasValue ? (draft, true) : (null, false);
        }
        catch (Exception ex)
        {
            // A deep link is a convenience; failing to resolve one must never cost the whole listing.
            _logger.LogDebug(ex, "Could not resolve a content node for {Host}{Path}.", host, path);
            return (null, false);
        }
    }

    /// <summary>Sampled successes share the table but are a denominator, never an error.</summary>
    private static bool IsErrorBucket(StatHourlyDto bucket) =>
        string.Equals(bucket.Type, OkType, StringComparison.Ordinal) is false;

    private List<StatHourlyDto> FetchHourly(DateTime fromUtc, DateTime toUtc, string? host)
    {
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        string table = syntax.GetQuotedTableName(StatHourlyDto.TableName_);
        string bucket = syntax.GetQuotedColumnName("bucketUtc");
        string hostColumn = syntax.GetQuotedColumnName("host");

        List<object> args = [fromUtc, toUtc];
        var where = $"WHERE {bucket} >= @0 AND {bucket} < @1";

        if (string.IsNullOrWhiteSpace(host) is false)
        {
            where += $" AND {hostColumn} = @2";
            args.Add(host);
        }

        return scope.Database.Fetch<StatHourlyDto>($"SELECT * FROM {table} {where}", args.ToArray());
    }

    /// <summary>Raw rows not yet folded in, so the dashboard can say the figures are still settling.</summary>
    private long CountPendingRaw()
    {
        long watermark = ReadWatermark();

        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        ISqlSyntaxProvider syntax = scope.SqlContext.SqlSyntax;

        return scope.Database.ExecuteScalar<long>(
            $"""
             SELECT COUNT(*) FROM {syntax.GetQuotedTableName(NelReportDto.TableName_)}
             WHERE {syntax.GetQuotedColumnName("id")} > @0
             """,
            watermark);
    }

    private long ReadWatermark()
    {
        string? value = _keyValueService.GetValue(WatermarkKey);
        if (value is null)
        {
            return 0;
        }

        string[] parts = value.Split('|');
        return long.TryParse(parts[0], out long watermark) ? watermark : 0;
    }

    private DateTime? ReadWatermarkTimestamp()
    {
        string? value = _keyValueService.GetValue(WatermarkKey);
        if (value is null)
        {
            return null;
        }

        // Invariant on both ends, and deliberately so. This is a machine-readable value in a settings
        // row, not something anybody reads: parsing it against the current culture round-trips only
        // for as long as the culture never changes, which is an assumption this package can no longer
        // make now that it renders for whatever language the reader has chosen.
        string[] parts = value.Split('|');
        return parts.Length > 1 && DateTime.TryParse(
            parts[1],
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTime timestamp)
            ? timestamp
            : null;
    }

    /// <summary>
    ///     Stores the watermark and the time it was set, packed into the single string
    ///     <c>IKeyValueService</c> offers.
    /// </summary>
    private void WriteWatermark(long watermark) =>
        _keyValueService.SetValue(
            WatermarkKey,
            string.Create(CultureInfo.InvariantCulture, $"{watermark}|{DateTime.UtcNow:O}"));

    /// <summary>Row shape for the grouped referrers query.</summary>
    private sealed class ReferrerRow
    {
        public string? Referrer { get; set; }

        public long Count { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }

    /// <summary>Row shape for the grouped offending-pages query.</summary>
    private sealed class OffendingPageRow
    {
        public string Host { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public int StatusCode { get; set; }

        public long Count { get; set; }

        public DateTime FirstSeenUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }
}
