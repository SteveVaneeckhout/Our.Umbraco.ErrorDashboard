using Our.Umbraco.ErrorDashboard.Persistence.Dtos;

namespace Our.Umbraco.ErrorDashboard.Reporting;

/// <summary>
///     In-memory buffer between the collector and the database.
/// </summary>
/// <remarks>
///     The collector must answer the browser immediately and must never let a slow database hold a
///     request open, so writes are handed off here and drained by <see cref="NelReportWriterService" />.
/// </remarks>
public interface INelReportQueue
{
    /// <summary>Queues a row. Returns false when the buffer is full and something had to be dropped.</summary>
    bool TryEnqueue(NelReportDto report);

    /// <summary>Reports dropped because the buffer was full, since process start.</summary>
    long DroppedCount { get; }
}
