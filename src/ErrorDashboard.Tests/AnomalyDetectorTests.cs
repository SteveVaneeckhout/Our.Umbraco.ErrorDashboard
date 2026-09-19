using System.Globalization;
using Our.Umbraco.ErrorDashboard.Alerting;

namespace Our.Umbraco.ErrorDashboard.Tests;

/// <summary>
///     Covers the decision that actually pages somebody.
/// </summary>
/// <remarks>
///     These cases are the reason the detector is a pure static function: the alternative way to
///     exercise them is to run a site for a fortnight and break it on purpose.
/// </remarks>
[TestClass]
public class AnomalyDetectorTests
{
    /// <summary>The shipped defaults. Tests that vary a threshold say so explicitly.</summary>
    private static readonly AnomalyThresholds Defaults = new(
        MinBaselineDays: 7,
        ScoreThreshold: 3.5,
        MinObserved: 10,
        MinRatio: 2.0);

    private static double[] Repeat(double value, int count) => [.. Enumerable.Repeat(value, count)];

    // ------------------------------------------------------------ the motivating cases

    [TestMethod]
    public void BusySite_SmallAbsoluteIncrease_DoesNotAlert()
    {
        // A site averaging 10,000 errors a day picking up another hundred is noise, and is exactly the
        // case a fixed "more than N errors" threshold gets wrong.
        double[] baseline = [9800, 10200, 9900, 10100, 10000, 10300, 9700, 10050, 9950, 10150, 9850, 10250, 9750, 10000];

        AnomalyResult result = AnomalyDetector.Evaluate(10_100, baseline, Defaults);

        Assert.IsFalse(result.IsAnomalous);
    }

    [TestMethod]
    public void QuietSite_LargeRelativeIncrease_Alerts()
    {
        // Fifty errors on a site that normally sees one or two is the case a fixed threshold tuned for
        // the busy site above would miss entirely.
        double[] baseline = [1, 0, 2, 1, 1, 0, 2, 1, 1, 2, 0, 1, 1, 2];

        AnomalyResult result = AnomalyDetector.Evaluate(50, baseline, Defaults);

        Assert.IsTrue(result.IsAnomalous);
        Assert.AreEqual(1, result.Median);
    }

    // ------------------------------------------------------------ the gates

    [TestMethod]
    public void ShortBaseline_NeverAlerts()
    {
        AnomalyResult result = AnomalyDetector.Evaluate(1000, [0, 0, 0], Defaults);

        Assert.IsFalse(result.IsAnomalous);
        Assert.AreEqual(AnomalyExplanation.InsufficientHistory, result.Explanation);
        CollectionAssert.AreEqual(new double[] { 3, Defaults.MinBaselineDays }, result.ExplanationArgs.ToArray());
    }

    [TestMethod]
    public void EmptyBaseline_NeverAlerts()
    {
        AnomalyResult result = AnomalyDetector.Evaluate(1000, [], Defaults);

        Assert.IsFalse(result.IsAnomalous);
        Assert.AreEqual(0, result.BaselineDays);
    }

    [TestMethod]
    public void SmallAbsoluteCount_DoesNotAlert_EvenOnAPerfectlyQuietSite()
    {
        // Three errors after a fortnight of none is a statistical mountain and an operational
        // non-event. MinObserved exists solely to stop this waking anyone.
        AnomalyResult result = AnomalyDetector.Evaluate(3, Repeat(0, 14), Defaults);

        Assert.IsFalse(result.IsAnomalous);
        Assert.AreEqual(AnomalyExplanation.BelowFloor, result.Explanation);
        CollectionAssert.AreEqual(new double[] { 3, Defaults.MinObserved }, result.ExplanationArgs.ToArray());
    }

    [TestMethod]
    public void QuietSite_GenuinelyBroken_Alerts()
    {
        AnomalyResult result = AnomalyDetector.Evaluate(60, Repeat(0, 14), Defaults);

        Assert.IsTrue(result.IsAnomalous);
    }

    [TestMethod]
    public void BelowMinimumRatio_DoesNotAlert()
    {
        // Comfortably over MinObserved and statistically interesting, but less than double the usual.
        double[] baseline = [20, 22, 19, 21, 20, 23, 18, 20, 21, 19, 22, 20, 21, 20];

        AnomalyResult result = AnomalyDetector.Evaluate(30, baseline, Defaults);

        Assert.IsFalse(result.IsAnomalous);
        Assert.AreEqual(AnomalyExplanation.BelowRatio, result.Explanation);
        CollectionAssert.AreEqual(new double[] { 30, Defaults.MinRatio, 20 }, result.ExplanationArgs.ToArray());
    }

    [TestMethod]
    public void ObservedBelowMedian_DoesNotAlert()
    {
        double[] baseline = Repeat(100, 14);

        AnomalyResult result = AnomalyDetector.Evaluate(20, baseline, Defaults);

        Assert.IsFalse(result.IsAnomalous);
        Assert.IsTrue(result.Score < 0);
    }

    [TestMethod]
    public void ZeroObserved_DoesNotAlert()
    {
        AnomalyResult result = AnomalyDetector.Evaluate(0, Repeat(5, 14), Defaults);

        Assert.IsFalse(result.IsAnomalous);
    }

    // ------------------------------------------------------------ scale fallbacks

    [TestMethod]
    public void FlatBaseline_UsesPoissonFloor_RatherThanDividingByZero()
    {
        // Every baseline day identical means MAD is zero and so is mean absolute deviation. Without the
        // sqrt(median + 1) floor the score would be infinite and every single blip would alert.
        double[] baseline = Repeat(4, 14);

        AnomalyResult result = AnomalyDetector.Evaluate(30, baseline, Defaults);

        Assert.AreEqual(Math.Sqrt(5), result.Scale, 1e-10);
        Assert.IsTrue(double.IsFinite(result.Score));
        Assert.IsTrue(result.IsAnomalous);
    }

    [TestMethod]
    public void MostlyFlatBaseline_FallsBackToMeanAbsoluteDeviation()
    {
        // More than half the days identical collapses MAD to zero, but the outlier days are real
        // information about this site's spread and should not be thrown away for a synthetic floor.
        double[] baseline = [2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 20, 30];

        AnomalyResult result = AnomalyDetector.Evaluate(40, baseline, Defaults);

        double expectedMeanAbsoluteDeviation = baseline.Select(value => Math.Abs(value - 2)).Average();
        Assert.AreEqual(1.2533 * expectedMeanAbsoluteDeviation, result.Scale, 1e-10);
    }

    [TestMethod]
    public void MadIsPreferredOverMeanAbsoluteDeviation_WhenBothAreAvailable()
    {
        double[] baseline = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14];

        AnomalyResult result = AnomalyDetector.Evaluate(100, baseline, Defaults);

        // Median 7.5, deviations {0.5,1.5,...,6.5}, whose own median is 3.5.
        Assert.AreEqual(7.5, result.Median);
        Assert.AreEqual(1.4826 * 3.5, result.Scale, 1e-10);
    }

    [TestMethod]
    public void EvenLengthBaseline_MedianIsTheMidpoint()
    {
        AnomalyResult result = AnomalyDetector.Evaluate(0, [1, 1, 1, 1, 3, 3, 3, 3], Defaults);

        Assert.AreEqual(2, result.Median);
    }

    [TestMethod]
    public void BaselineOrderDoesNotMatter()
    {
        double[] ascending = [0, 1, 1, 2, 2, 3, 4, 40];
        double[] shuffled = [40, 2, 0, 4, 1, 3, 1, 2];

        AnomalyResult fromAscending = AnomalyDetector.Evaluate(50, ascending, Defaults);
        AnomalyResult fromShuffled = AnomalyDetector.Evaluate(50, shuffled, Defaults);

        Assert.AreEqual(fromAscending.Score, fromShuffled.Score, 1e-10);
        Assert.AreEqual(fromAscending.IsAnomalous, fromShuffled.IsAnomalous);
    }

    [TestMethod]
    public void Explanation_SaysWhichGateHeld_WithTheFiguresBehindIt()
    {
        // One case per gate, in the order the gates are applied.
        (AnomalyResult Result, AnomalyExplanation Expected)[] cases =
        [
            (AnomalyDetector.Evaluate(1000, [0, 0], Defaults), AnomalyExplanation.InsufficientHistory),
            (AnomalyDetector.Evaluate(3, Repeat(0, 14), Defaults), AnomalyExplanation.BelowFloor),
            (AnomalyDetector.Evaluate(30, Repeat(20, 14), Defaults), AnomalyExplanation.BelowRatio),
            (AnomalyDetector.Evaluate(50, Repeat(1, 14), Defaults), AnomalyExplanation.Anomalous),
        ];

        foreach ((AnomalyResult Result, AnomalyExplanation Expected) entry in cases)
        {
            Assert.AreEqual(entry.Expected, entry.Result.Explanation);
        }

        // The args are what the dashboard interpolates into the translated sentence, so an empty list
        // would render a message with holes in it rather than fall back to anything.
        foreach ((AnomalyResult Result, AnomalyExplanation Expected) entry in cases)
        {
            Assert.IsNotEmpty(entry.Result.ExplanationArgs);
        }
    }

    [TestMethod]
    public void WithinSpread_IsReportedWithTheScoreAndItsThreshold()
    {
        // Over the floor and over twice the median, but not far enough out of the site's own spread.
        double[] baseline = [10, 30, 10, 30, 10, 30, 10, 30, 10, 30, 10, 30, 10, 30];

        AnomalyResult result = AnomalyDetector.Evaluate(70, baseline, Defaults);

        Assert.IsFalse(result.IsAnomalous);
        Assert.AreEqual(AnomalyExplanation.WithinSpread, result.Explanation);
        CollectionAssert.AreEqual(new double[] { result.Score, Defaults.ScoreThreshold }, result.ExplanationArgs.ToArray());
    }

    [TestMethod]
    public void ToLogString_IsInvariant_WhateverTheAmbientCulture()
    {
        // The log viewer is an operator surface. A line whose decimal separator depended on whichever
        // user's request happened to trigger the job would be a nightmare to search.
        AnomalyResult result = AnomalyDetector.Evaluate(50, Repeat(1, 14), Defaults);

        // A fractional score is the point: under nl-NL, current-culture formatting would render it
        // "34,64" where the invariant form is "34.64".
        Assert.AreNotEqual(Math.Floor(result.Score), result.Score);

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            string log = result.ToLogString();

            Assert.Contains($"score {result.Score.ToString("0.##", CultureInfo.InvariantCulture)}", log);
            Assert.Contains("Anomalous", log);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
