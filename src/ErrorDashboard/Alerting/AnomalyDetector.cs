using System.Globalization;
using System.Text.Json.Serialization;

namespace Our.Umbraco.ErrorDashboard.Alerting;

/// <summary>
///     Decides whether an error count is abnormal for this particular site.
/// </summary>
/// <remarks>
///     <para>
///         Uses the modified z-score of Iglewicz and Hoaglin: the distance from the median, scaled by
///         the median absolute deviation. The reason it suits error counts is that it needs no traffic
///         figure to normalise against - MAD measures the site's *own* day-to-day variability, so a
///         busy site with a noisy baseline gets a wide band and a quiet site gets a narrow one, with no
///         per-site tuning. The mean and standard deviation would not work here: a single bad day
///         drags both, so one outage teaches the detector to ignore the next one.
///     </para>
///     <para>
///         Pure and static, with the clock and every threshold passed in. All of the risk in this
///         feature is concentrated in this file, and it is the only part that can be exercised
///         completely without waiting hours for real browser traffic.
///     </para>
/// </remarks>
public static class AnomalyDetector
{
    /// <summary>
    ///     Scales MAD to a standard-deviation equivalent for normally distributed data
    ///     (1 / 0.6745). The conventional constant, and what makes a 3.5 threshold mean what the
    ///     literature says it means.
    /// </summary>
    private const double MadToSigma = 1.4826;

    /// <summary>
    ///     Equivalent constant for mean absolute deviation (sqrt(pi / 2)), used when MAD collapses to
    ///     zero because more than half the baseline days are identical.
    /// </summary>
    private const double MeanAdToSigma = 1.2533;

    /// <summary>
    ///     Evaluates one observation against its baseline.
    /// </summary>
    /// <param name="observed">Errors in the window being judged.</param>
    /// <param name="baseline">
    ///     One value per preceding complete day. Order is irrelevant - only the distribution matters.
    /// </param>
    /// <param name="thresholds">Tuning knobs, straight from configuration.</param>
    public static AnomalyResult Evaluate(double observed, IReadOnlyList<double> baseline, AnomalyThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (baseline.Count < thresholds.MinBaselineDays)
        {
            return AnomalyResult.NotAnomalous(
                observed,
                median: 0,
                scale: 0,
                score: 0,
                baseline.Count,
                AnomalyExplanation.InsufficientHistory,
                [baseline.Count, thresholds.MinBaselineDays]);
        }

        double[] sorted = [.. baseline.Order()];
        double median = Median(sorted);
        double scale = EstimateScale(sorted, median);
        double score = (observed - median) / scale;

        // Every gate below is a separate reason not to wake somebody up, so report which one held.
        if (observed < thresholds.MinObserved)
        {
            return AnomalyResult.NotAnomalous(
                observed,
                median,
                scale,
                score,
                baseline.Count,
                AnomalyExplanation.BelowFloor,
                [observed, thresholds.MinObserved]);
        }

        // A median of zero would make any positive count an infinite ratio, so treat "no errors at all"
        // as one error for this comparison.
        double ratioBase = Math.Max(median, 1);
        if (observed < thresholds.MinRatio * ratioBase)
        {
            return AnomalyResult.NotAnomalous(
                observed,
                median,
                scale,
                score,
                baseline.Count,
                AnomalyExplanation.BelowRatio,
                [observed, thresholds.MinRatio, median]);
        }

        if (score < thresholds.ScoreThreshold)
        {
            return AnomalyResult.NotAnomalous(
                observed,
                median,
                scale,
                score,
                baseline.Count,
                AnomalyExplanation.WithinSpread,
                [score, thresholds.ScoreThreshold]);
        }

        return new AnomalyResult(
            true,
            observed,
            median,
            scale,
            score,
            baseline.Count,
            AnomalyExplanation.Anomalous,
            [observed, median, score]);
    }

    /// <summary>
    ///     Robust spread estimate, with two fallbacks.
    /// </summary>
    /// <remarks>
    ///     MAD is zero whenever more than half the baseline days share a value, which on a healthy site
    ///     is the common case rather than an edge case - fourteen days of zero errors is exactly that.
    ///     Without a fallback the score would be infinite and every blip would alert. Mean absolute
    ///     deviation catches the "mostly identical, occasionally not" shape; the Poisson-style
    ///     sqrt(median + 1) floor catches a baseline that is perfectly flat, where counting statistics
    ///     are the only spread there is.
    /// </remarks>
    private static double EstimateScale(double[] sortedBaseline, double median)
    {
        double[] deviations = [.. sortedBaseline.Select(value => Math.Abs(value - median)).Order()];
        double mad = Median(deviations);

        if (mad > 0)
        {
            return MadToSigma * mad;
        }

        double meanAbsoluteDeviation = deviations.Average();
        if (meanAbsoluteDeviation > 0)
        {
            return MeanAdToSigma * meanAbsoluteDeviation;
        }

        return Math.Sqrt(median + 1);
    }

    /// <summary>Median of an already-sorted, non-empty sequence.</summary>
    private static double Median(double[] sorted)
    {
        int count = sorted.Length;
        int middle = count / 2;

        return count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}

/// <summary>Thresholds governing <see cref="AnomalyDetector.Evaluate" />.</summary>
/// <param name="MinBaselineDays">Days of history below which nothing is ever judged anomalous.</param>
/// <param name="ScoreThreshold">Modified z-score at which an observation counts as anomalous.</param>
/// <param name="MinObserved">Absolute floor, so a quiet site going from 0 to 3 never alerts.</param>
/// <param name="MinRatio">Multiple of the median the observation must also reach.</param>
public sealed record AnomalyThresholds(
    int MinBaselineDays,
    double ScoreThreshold,
    int MinObserved,
    double MinRatio);

/// <summary>
///     Which gate decided the verdict.
/// </summary>
/// <remarks>
///     The detector reports *which* reason applied and the numbers behind it, rather than a finished
///     English sentence. The sentence is a UI concern, and the browser is the only place that knows
///     the editor's language and how to format a decimal for it. The client maps each member onto a
///     dictionary key, and because the map is typed on this enum, adding a member here is a
///     compile error there until it is translated.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AnomalyExplanation>))]
public enum AnomalyExplanation
{
    /// <summary>Too few baseline days to judge anything. Args: days of history, days required.</summary>
    InsufficientHistory,

    /// <summary>Below the absolute floor. Args: observed, floor.</summary>
    BelowFloor,

    /// <summary>Not a large enough multiple of the usual count. Args: observed, minimum ratio, median.</summary>
    BelowRatio,

    /// <summary>Within the site's normal spread. Args: score, score threshold.</summary>
    WithinSpread,

    /// <summary>Every gate passed. Args: observed, median, score.</summary>
    Anomalous,
}

/// <summary>
///     Outcome of one evaluation. Carries the working, not just the verdict - the numbers go into the
///     alert email and the history table so a past decision can still be explained, and so somebody
///     can tell whether the thresholds need moving.
/// </summary>
/// <param name="ExplanationArgs">
///     The figures behind <paramref name="Explanation" />, in the order that reason's message expects
///     them. Raw numbers rather than formatted text: whoever renders them - the browser, or the email
///     builder - formats them for the culture of the person reading.
/// </param>
public sealed record AnomalyResult(
    bool IsAnomalous,
    double Observed,
    double Median,
    double Scale,
    double Score,
    int BaselineDays,
    AnomalyExplanation Explanation,
    IReadOnlyList<double> ExplanationArgs)
{
    internal static AnomalyResult NotAnomalous(
        double observed,
        double median,
        double scale,
        double score,
        int baselineDays,
        AnomalyExplanation explanation,
        IReadOnlyList<double> explanationArgs) =>
        new(false, observed, median, scale, score, baselineDays, explanation, explanationArgs);

    /// <summary>
    ///     A fixed-English one-liner for logs. Never shown to a user - the log viewer is an operator
    ///     surface, and a log line that changed language with whoever happened to trigger it would be
    ///     worse than useless when searching.
    /// </summary>
    public string ToLogString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Explanation} (observed {Observed:0.##}, median {Median:0.##}, score {Score:0.##}, {BaselineDays} baseline day(s))");
}
