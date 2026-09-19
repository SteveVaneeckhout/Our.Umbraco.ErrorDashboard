using System.Threading.Channels;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;

namespace Our.Umbraco.ErrorDashboard.Reporting;

/// <inheritdoc cref="INelReportQueue" />
public sealed class NelReportQueue : INelReportQueue
{
    /// <summary>
    ///     Roughly a minute of a very bad day. Bounded deliberately: an unbounded queue in front of a
    ///     database that has stopped responding is just a slower way to run out of memory.
    /// </summary>
    private const int Capacity = 10_000;

    private readonly Channel<NelReportDto> _channel;

    private long _dropped;

    public NelReportQueue()
    {
        _channel = Channel.CreateBounded<NelReportDto>(
            new BoundedChannelOptions(Capacity)
            {
                // Losing the oldest telemetry always beats blocking a visitor's request or refusing the
                // newest reports, which are the ones describing whatever is breaking right now.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            // DropOldest makes TryWrite always succeed, so an eviction is invisible at the call site.
            // This callback is the only place a shed report can be counted.
            itemDropped: _ => Interlocked.Increment(ref _dropped));
    }

    /// <inheritdoc />
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>The drain side. Internal - only the writer service consumes it.</summary>
    internal ChannelReader<NelReportDto> Reader => _channel.Reader;

    /// <inheritdoc />
    public bool TryEnqueue(NelReportDto report) => _channel.Writer.TryWrite(report);
}
