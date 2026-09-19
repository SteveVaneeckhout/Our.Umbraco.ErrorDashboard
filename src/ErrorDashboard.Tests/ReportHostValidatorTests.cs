using Our.Umbraco.ErrorDashboard.Reporting;

namespace Our.Umbraco.ErrorDashboard.Tests;

/// <summary>
///     Covers how an Umbraco domain entry is reduced to the hostname the collector compares against.
/// </summary>
/// <remarks>
///     Editors type these by hand and Umbraco stores them verbatim, so the range of shapes is wide.
///     Getting this wrong fails in one of two bad directions: too strict silently discards every
///     report from a legitimate hostname, too loose reopens the collector to anyone.
/// </remarks>
[TestClass]
public class ReportHostValidatorTests
{
    [TestMethod]
    // Full URLs, which is what the backoffice writes when an editor pastes one.
    [DataRow("https://example.com", "example.com")]
    [DataRow("https://example.com:44366", "example.com")]
    [DataRow("http://example.com/", "example.com")]
    [DataRow("https://test.local.steve.rocks", "test.local.steve.rocks")]
    // Bare hostnames, with and without a port.
    [DataRow("example.com", "example.com")]
    [DataRow("example.com:8080", "example.com")]
    [DataRow("localhost:44366", "localhost")]
    // Wildcards: the stem is what a report's host can actually match.
    [DataRow("*.example.com", "example.com")]
    // Surrounding whitespace from a copy-paste.
    [DataRow("  example.com  ", "example.com")]
    public void DomainEntry_IsReducedToItsHostname(string domainName, string expected)
    {
        Assert.AreEqual(expected, ReportHostValidator.NormaliseDomainToHost(domainName));
    }

    [TestMethod]
    // Path-only culture prefixes name no host at all.
    [DataRow("/en")]
    [DataRow("/en-us/")]
    // Nothing usable.
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("*")]
    public void EntriesThatNameNoHost_AreSkipped(string? domainName)
    {
        Assert.IsNull(ReportHostValidator.NormaliseDomainToHost(domainName));
    }

    [TestMethod]
    public void PortsAreDropped_SoASiteOnANonStandardPortStillMatches()
    {
        // This repo's own domain is stored as https://localhost:44366, while a report's url yields the
        // bare host. Without dropping the port, nothing from this site would ever be accepted.
        Assert.AreEqual(
            ReportHostValidator.NormaliseDomainToHost("localhost"),
            ReportHostValidator.NormaliseDomainToHost("https://localhost:44366"));
    }

    [TestMethod]
    public void Ipv6Literal_KeepsItsBrackets()
    {
        // Uri.Host round-trips an IPv6 literal in brackets, which is also how it appears in a report url.
        Assert.AreEqual("[::1]", ReportHostValidator.NormaliseDomainToHost("https://[::1]:44366"));
    }
}
