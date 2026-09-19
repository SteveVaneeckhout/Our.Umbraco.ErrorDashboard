using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;
using Our.Umbraco.ErrorDashboard.Reporting;

namespace Our.Umbraco.ErrorDashboard.Tests;

/// <summary>
///     Covers the validation rules on the collector.
/// </summary>
/// <remarks>
///     That endpoint is unauthenticated and reachable from the internet, so these rules are the only
///     thing deciding what reaches the database.
/// </remarks>
[TestClass]
public class NelReportParserTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stands in for the real validator, which reads Umbraco's configured domains.</summary>
    private static bool OwnHost(string host) =>
        host is "example.com" or "cdn.example.com";

    private static ErrorDashboardOptions Options(Action<ErrorDashboardOptions>? configure = null)
    {
        var options = new ErrorDashboardOptions();
        configure?.Invoke(options);
        return options;
    }

    private static NelReportEnvelope HttpError(
        int statusCode = 404,
        string url = "https://example.com/missing",
        long age = 0,
        string type = "http.error") =>
        new()
        {
            Age = age,
            Type = "network-error",
            Url = url,
            Body = new NelReportBody
            {
                Type = type,
                Phase = "application",
                StatusCode = statusCode,
                Method = "GET",
                Protocol = "h2",
                ServerIp = "203.0.113.1",
                ElapsedTime = 120,
                SamplingFraction = 1.0,
            },
        };

    // ------------------------------------------------------------ accepted

    [TestMethod]
    public void Http404_IsStored()
    {
        NelReportDto? row = NelReportParser.TryMap(HttpError(), Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.AreEqual(404, row.StatusCode);
        Assert.AreEqual("http.error", row.Type);
        Assert.AreEqual("example.com", row.Host);
        Assert.AreEqual("/missing", row.Path);
        Assert.IsFalse(row.IsSuccess);
    }

    [TestMethod]
    [DataRow("tls.cert.date_invalid")]
    [DataRow("dns.name_not_resolved")]
    [DataRow("tcp.timed_out")]
    public void TransportFailures_AreStored_WithNoStatusCode(string type)
    {
        var envelope = new NelReportEnvelope
        {
            Type = "network-error",
            Url = "https://example.com/page",
            Body = new NelReportBody { Type = type, Phase = "connection", SamplingFraction = 1.0 },
        };

        NelReportDto? row = NelReportParser.TryMap(envelope, Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.AreEqual(type, row.Type);
        Assert.IsNull(row.StatusCode);
    }

    [TestMethod]
    public void QueryString_IsStripped()
    {
        // The same broken page otherwise appears once per tracking parameter, and query strings are the
        // part of a URL most likely to carry something personal.
        NelReportDto? row = NelReportParser.TryMap(
            HttpError(url: "https://example.com/missing?utm_source=news&id=42"), Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.AreEqual("/missing", row.Path);
        Assert.AreEqual("https://example.com/missing", row.Url);
    }

    [TestMethod]
    public void Age_IsSubtractedFromReceiptTime()
    {
        // Reports arrive minutes late. Bucketing on receipt would pile a delayed batch onto the wrong
        // hour and manufacture a spike out of nothing.
        NelReportDto? row = NelReportParser.TryMap(HttpError(age: 90_000), Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.AreEqual(Now.AddSeconds(-90), row.OccurredUtc);
        Assert.AreEqual(Now, row.ReceivedUtc);
    }

    [TestMethod]
    public void AbsurdAge_IsClampedToTheRetentionWindow()
    {
        NelReportDto? row = NelReportParser.TryMap(
            HttpError(age: (long)TimeSpan.FromDays(3650).TotalMilliseconds),
            Options(o => o.RawRetentionDays = 21),
            OwnHost,
            Now);

        Assert.IsNotNull(row);
        Assert.AreEqual(Now.AddDays(-21), row.OccurredUtc);
    }

    [TestMethod]
    public void NegativeAge_IsClampedToZero()
    {
        NelReportDto? row = NelReportParser.TryMap(HttpError(age: -5_000), Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.AreEqual(Now, row.OccurredUtc);
    }

    [TestMethod]
    public void AnyHostTheValidatorAccepts_IsStored()
    {
        NelReportDto? row = NelReportParser.TryMap(
            HttpError(url: "https://cdn.example.com/asset.js"),
            Options(),
            OwnHost,
            Now);

        Assert.IsNotNull(row);
        Assert.AreEqual("cdn.example.com", row.Host);
    }

    // ------------------------------------------------------------ rejected

    [TestMethod]
    public void ForeignHost_IsRejected()
    {
        // Without this the collector is a free logging service for anybody who finds the URL.
        NelReportDto? row = NelReportParser.TryMap(
            HttpError(url: "https://evil.example.net/harvest"), Options(), OwnHost, Now);

        Assert.IsNull(row);
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(301)]
    [DataRow(399)]
    public void HttpErrorBelow400_IsRejected(int statusCode)
    {
        Assert.IsNull(NelReportParser.TryMap(HttpError(statusCode), Options(), OwnHost, Now));
    }

    [TestMethod]
    public void SuccessReport_IsRejected_WhenSuccessSamplingIsOff()
    {
        var envelope = new NelReportEnvelope
        {
            Type = "network-error",
            Url = "https://example.com/fine",
            Body = new NelReportBody { Type = "ok", Phase = "application", StatusCode = 200, SamplingFraction = 0.01 },
        };

        Assert.IsNull(NelReportParser.TryMap(envelope, Options(o => o.SuccessFraction = 0), OwnHost, Now));
    }

    [TestMethod]
    public void SuccessReport_IsStored_WhenSuccessSamplingIsOn()
    {
        // These carry no error, only a traffic denominator, so the weight matters and the count does not.
        var envelope = new NelReportEnvelope
        {
            Type = "network-error",
            Url = "https://example.com/fine",
            Body = new NelReportBody { Type = "ok", Phase = "application", StatusCode = 200, SamplingFraction = 0.01 },
        };

        NelReportDto? row = NelReportParser.TryMap(envelope, Options(o => o.SuccessFraction = 0.01), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.IsTrue(row.IsSuccess);
        Assert.AreEqual(0.01, row.SamplingFraction);
    }

    [TestMethod]
    public void NonNetworkErrorReport_IsRejected()
    {
        // The Reporting API multiplexes deprecation and intervention reports down the same endpoint.
        var envelope = HttpError();
        envelope.Type = "deprecation";

        Assert.IsNull(NelReportParser.TryMap(envelope, Options(), OwnHost, Now));
    }

    [TestMethod]
    public void MissingBody_IsRejected()
    {
        var envelope = HttpError();
        envelope.Body = null;

        Assert.IsNull(NelReportParser.TryMap(envelope, Options(), OwnHost, Now));
    }

    [TestMethod]
    public void NullEnvelope_IsRejected()
    {
        // A payload of `[null]` parses cleanly and must not reach the database.
        Assert.IsNull(NelReportParser.TryMap(null, Options(), OwnHost, Now));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not a url")]
    [DataRow("/relative/only")]
    public void UnusableUrl_IsRejected(string? url)
    {
        var envelope = HttpError();
        envelope.Url = url;

        Assert.IsNull(NelReportParser.TryMap(envelope, Options(), OwnHost, Now));
    }

    [TestMethod]
    public void EmptyBodyType_IsRejected()
    {
        var envelope = HttpError();
        envelope.Body!.Type = "";

        Assert.IsNull(NelReportParser.TryMap(envelope, Options(), OwnHost, Now));
    }

    // ------------------------------------------------------------ field handling

    [TestMethod]
    public void OverlongFields_AreTruncatedRatherThanFailingTheInsert()
    {
        var envelope = HttpError(url: $"https://example.com/{new string('a', 3000)}");
        envelope.Body!.Referrer = new string('r', 5000);

        NelReportDto? row = NelReportParser.TryMap(envelope, Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.IsTrue(row.Url.Length <= 2048);
        Assert.IsTrue(row.Path.Length <= 850);
        Assert.IsTrue(row.Referrer!.Length <= 1024);
    }

    [TestMethod]
    public void BlankOptionalFields_BecomeNull()
    {
        var envelope = HttpError();
        envelope.Body!.Referrer = "";
        envelope.Body.Protocol = "   ";

        NelReportDto? row = NelReportParser.TryMap(envelope, Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.IsNull(row.Referrer);
        Assert.IsNull(row.Protocol);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(2.0)]
    public void InvalidSamplingFraction_DefaultsToOne(double? fraction)
    {
        // Weighted counts divide by this, so a zero or negative value would produce an infinity that
        // then poisons every aggregate it is summed into.
        var envelope = HttpError();
        envelope.Body!.SamplingFraction = fraction;

        NelReportDto? row = NelReportParser.TryMap(envelope, Options(), OwnHost, Now);

        Assert.IsNotNull(row);
        Assert.AreEqual(1.0, row.SamplingFraction);
    }

    [TestMethod]
    public void HostAndPath_AreMatchedCaseInsensitively_ButHashIsStable()
    {
        NelReportDto? lower = NelReportParser.TryMap(
            HttpError(url: "https://EXAMPLE.COM/missing"), Options(), OwnHost, Now);

        Assert.IsNotNull(lower);
        // Uri lowercases the host for us, so the same page always hashes to the same bucket.
        Assert.AreEqual("example.com", lower.Host);
        Assert.AreEqual(NelReportParser.Hash("example.com/missing"), lower.UrlHash);
    }

    [TestMethod]
    public void ToUtcHour_TruncatesToTheHour()
    {
        DateTime bucket = NelReportParser.ToUtcHour(new DateTime(2026, 9, 12, 13, 47, 31, DateTimeKind.Utc));

        Assert.AreEqual(new DateTime(2026, 9, 12, 13, 0, 0, DateTimeKind.Utc), bucket);
    }

    [TestMethod]
    [DataRow("http.error", 500, true)]
    [DataRow("http.error", 404, true)]
    [DataRow("http.error", 200, false)]
    [DataRow("http.error", null, false)]
    [DataRow("http.protocol.error", 0, false)]
    [DataRow("tls.failed", null, true)]
    [DataRow("dns.unreachable", null, true)]
    [DataRow("tcp.refused", null, true)]
    [DataRow("something.else", null, false)]
    public void IsInteresting_ClassifiesTypesAsDocumented(string type, int? statusCode, bool expected)
    {
        Assert.AreEqual(expected, NelReportParser.IsInteresting(type, statusCode));
    }
}
