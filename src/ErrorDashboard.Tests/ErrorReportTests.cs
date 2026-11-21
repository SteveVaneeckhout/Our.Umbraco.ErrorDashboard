using System.Reflection;
using Our.Umbraco.ErrorDashboard.Notifications;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;

namespace Our.Umbraco.ErrorDashboard.Tests;

/// <summary>
///     Covers the copy handed to <see cref="ErrorReportsReceivedNotification" /> handlers.
/// </summary>
[TestClass]
public class ErrorReportTests
{
    /// <summary>Row-keeping columns with no meaning outside the table.</summary>
    private static readonly string[] StorageOnly =
    [
        nameof(NelReportDto.Id),
        nameof(NelReportDto.UrlHash),
        nameof(NelReportDto.Source),
    ];

    private static NelReportDto Row() =>
        new()
        {
            Id = 42,
            ReceivedUtc = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc),
            OccurredUtc = new DateTime(2026, 9, 12, 11, 58, 0, DateTimeKind.Utc),
            Host = "example.com",
            Path = "/missing",
            Url = "https://example.com/missing",
            UrlHash = "abc",
            Type = "http.error",
            Phase = "application",
            StatusCode = 404,
            Method = "GET",
            Protocol = "h2",
            ServerIp = "203.0.113.7",
            Referrer = "https://example.com/",
            ElapsedTimeMs = 120,
            SamplingFraction = 0.25,
            IsSuccess = false,
        };

    [TestMethod]
    public void FromDto_CopiesEveryFieldAHandlerCanUse()
    {
        NelReportDto row = Row();

        ErrorReport report = ErrorReport.FromDto(row);

        Assert.AreEqual(row.ReceivedUtc, report.ReceivedUtc);
        Assert.AreEqual(row.OccurredUtc, report.OccurredUtc);
        Assert.AreEqual("example.com", report.Host);
        Assert.AreEqual("/missing", report.Path);
        Assert.AreEqual("https://example.com/missing", report.Url);
        Assert.AreEqual("http.error", report.Type);
        Assert.AreEqual("application", report.Phase);
        Assert.AreEqual(404, report.StatusCode);
        Assert.AreEqual("GET", report.Method);
        Assert.AreEqual("h2", report.Protocol);
        Assert.AreEqual("203.0.113.7", report.ServerIp);
        Assert.AreEqual("https://example.com/", report.Referrer);
        Assert.AreEqual(120, report.ElapsedTimeMs);
        Assert.AreEqual(0.25, report.SamplingFraction);
        Assert.IsFalse(report.IsSuccess);
    }

    /// <summary>
    ///     A column added to the table later should either reach handlers or be listed as storage-only
    ///     on purpose - not be forgotten.
    /// </summary>
    [TestMethod]
    public void EveryStoredColumn_IsExposedOrDeliberatelyKeptBack()
    {
        HashSet<string> exposed = typeof(ErrorReport)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet();

        string[] missing = typeof(NelReportDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => !exposed.Contains(name) && !StorageOnly.Contains(name))
            .ToArray();

        Assert.IsEmpty(missing, $"Not on {nameof(ErrorReport)}: {string.Join(", ", missing)}");
    }

    [TestMethod]
    public void Weight_IsTheNumberOfRequestsASampledReportStandsFor()
    {
        ErrorReport sampled = ErrorReport.FromDto(Row());
        ErrorReport unsampled = sampled with { SamplingFraction = 1.0 };

        Assert.AreEqual(4.0, sampled.Weight, 1e-9);
        Assert.AreEqual(1.0, unsampled.Weight, 1e-9);
    }
}
