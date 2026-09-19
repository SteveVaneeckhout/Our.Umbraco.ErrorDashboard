using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPoco;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Our.Umbraco.ErrorDashboard.Reporting;

/// <summary>
///     Drains <see cref="NelReportQueue" /> into the raw reports table in batches.
/// </summary>
/// <remarks>
///     Batching is the point. A busy site can produce a steady trickle of reports, and one round trip
///     per report would put the database in the request path of every broken asset on the site.
/// </remarks>
public sealed class NelReportWriterService : BackgroundService
{
    /// <summary>Rows per insert. Large enough to amortise the round trip, small enough to stay responsive.</summary>
    private const int BatchSize = 500;

    /// <summary>How long a partial batch waits for company before being written anyway.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>Backoff after a failed write, so a database outage does not become a hot loop.</summary>
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

    private readonly ILogger<NelReportWriterService> _logger;
    private readonly NelReportQueue _queue;
    private readonly IRuntimeState _runtimeState;
    private readonly IScopeProvider _scopeProvider;

    public NelReportWriterService(
        NelReportQueue queue,
        IScopeProvider scopeProvider,
        IRuntimeState runtimeState,
        ILogger<NelReportWriterService> logger)
    {
        _queue = queue;
        _scopeProvider = scopeProvider;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<NelReportDto> batch = new(BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FillBatchAsync(batch, stoppingToken);

                if (batch.Count == 0)
                {
                    continue;
                }

                // On the first boot after this package is installed, Umbraco runs package migrations in
                // a background service *after* Kestrel starts - so the collector is already accepting
                // POSTs while these tables do not exist yet. Holding the rows in the batch until the
                // runtime settles means those early reports are written rather than lost.
                if (_runtimeState.Level != RuntimeLevel.Run)
                {
                    await Task.Delay(FlushInterval, stoppingToken);
                    continue;
                }

                Write(batch);
                batch.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad batch must not kill the drain loop, or the queue silently stops emptying for
                // the rest of the process lifetime.
                _logger.LogError(ex, "Failed to persist {Count} network error report(s); discarding the batch.", batch.Count);
                batch.Clear();

                try
                {
                    await Task.Delay(ErrorBackoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Best effort on shutdown: whatever is already queued is cheap to write and awkward to lose.
        if (batch.Count > 0 && _runtimeState.Level == RuntimeLevel.Run)
        {
            try
            {
                Write(batch);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not flush {Count} buffered report(s) during shutdown.", batch.Count);
            }
        }
    }

    /// <summary>
    ///     Waits for at least one row, then takes everything already queued up to the batch size. The
    ///     initial await is what keeps this from spinning while the site is idle.
    /// </summary>
    private async Task FillBatchAsync(List<NelReportDto> batch, CancellationToken stoppingToken)
    {
        if (batch.Count == 0)
        {
            using var flushTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            flushTimeout.CancelAfter(FlushInterval);

            try
            {
                NelReportDto first = await _queue.Reader.ReadAsync(flushTimeout.Token);
                batch.Add(first);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Idle tick, nothing arrived.
                return;
            }
        }

        while (batch.Count < BatchSize && _queue.Reader.TryRead(out NelReportDto? next))
        {
            batch.Add(next);
        }
    }

    private void Write(List<NelReportDto> batch)
    {
        using IScope scope = _scopeProvider.CreateScope();

        // InsertBatch rather than Umbraco's BulkInsertRecords: on SQL Server the latter goes through
        // SqlBulkCopy, whose schema reader refuses a FLOAT column carrying no explicit precision -
        // which is exactly what a `double` maps to. NPoco's multi-row INSERT has no such quirk, behaves
        // the same on every provider, and at this batch size the throughput difference is noise.
        scope.Database.InsertBatch(batch, new BatchOptions { BatchSize = BatchSize });
        scope.Complete();

        _logger.LogDebug("Persisted {Count} network error report(s).", batch.Count);
    }
}
