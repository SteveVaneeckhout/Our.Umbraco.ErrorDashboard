using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Our.Umbraco.ErrorDashboard.Configuration;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Our.Umbraco.ErrorDashboard.Reporting;

/// <summary>
///     Builds the set of hosts the collector will accept reports about, from Umbraco's own configured
///     domains plus anything named in configuration.
/// </summary>
/// <remarks>
///     <para>
///         The obvious check - compare the report's host against the request's <c>Host</c> header - is
///         worthless on its own, because that header is supplied by whoever is calling. ASP.NET Core
///         only rejects an unrecognised <c>Host</c> when <c>AllowedHosts</c> is configured, and it is
///         not by default, so a single curl with <c>-H "Host: anything"</c> defeats it. Umbraco's
///         domain list is the authoritative answer to "which hostnames is this site", so it is what
///         gets used.
///     </para>
///     <para>
///         When no domains are configured - a single-site install, or local development - there is
///         nothing authoritative to check against and the validator falls back to the request host.
///         That fallback is as weak as the check above; <see cref="KnownHosts" /> is empty in that
///         mode so the dashboard can say so, and the README recommends setting either an Umbraco
///         domain or ASP.NET Core's own <c>AllowedHosts</c>.
///     </para>
/// </remarks>
public sealed class ReportHostValidator : IReportHostValidator
{
    /// <summary>Domains change rarely and this runs per report, so a short cache is worth it.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly IDomainService _domainService;
    private readonly Lock _lock = new();
    private readonly ILogger<ReportHostValidator> _logger;
    private readonly IOptionsMonitor<ErrorDashboardOptions> _options;
    private readonly IRuntimeState _runtimeState;

    private DateTime _cachedUntilUtc = DateTime.MinValue;
    private HashSet<string> _cachedHosts = new(StringComparer.OrdinalIgnoreCase);

    public ReportHostValidator(
        IDomainService domainService,
        IRuntimeState runtimeState,
        IOptionsMonitor<ErrorDashboardOptions> options,
        ILogger<ReportHostValidator> logger)
    {
        _domainService = domainService;
        _runtimeState = runtimeState;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> KnownHosts => [.. GetAcceptedHosts().Order()];

    /// <inheritdoc />
    public bool IsAccepted(string reportHost, string requestHost)
    {
        if (string.IsNullOrWhiteSpace(reportHost))
        {
            return false;
        }

        HashSet<string> accepted = GetAcceptedHosts();

        if (accepted.Count > 0)
        {
            return accepted.Contains(reportHost);
        }

        // Nothing authoritative to check against. Fall back to the request's own host, which is only
        // as trustworthy as whatever host filtering sits in front of this application.
        return string.Equals(reportHost, requestHost, StringComparison.OrdinalIgnoreCase);
    }

    private HashSet<string> GetAcceptedHosts()
    {
        DateTime now = DateTime.UtcNow;

        lock (_lock)
        {
            if (now < _cachedUntilUtc)
            {
                return _cachedHosts;
            }
        }

        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);

        foreach (string configured in _options.CurrentValue.AllowedHosts)
        {
            AddHost(hosts, configured);
        }

        // Reading domains needs the database, which is not there yet while package migrations run.
        if (_runtimeState.Level == RuntimeLevel.Run)
        {
            try
            {
                IEnumerable<IDomain> domains = _domainService.GetAllAsync(true).GetAwaiter().GetResult();
                foreach (IDomain domain in domains)
                {
                    AddHost(hosts, domain.DomainName);
                }
            }
            catch (Exception ex)
            {
                // Failing open would turn the collector into a public logging service, so a failure
                // here leaves the set as-is and the fallback comparison applies.
                _logger.LogWarning(ex, "Could not read Umbraco domains while validating a report host.");
            }
        }

        lock (_lock)
        {
            _cachedHosts = hosts;
            _cachedUntilUtc = now.Add(CacheLifetime);
            return _cachedHosts;
        }
    }

    private static void AddHost(HashSet<string> hosts, string? domainName)
    {
        string? host = NormaliseDomainToHost(domainName);
        if (host is not null)
        {
            hosts.Add(host);
        }
    }

    /// <summary>
    ///     Normalises one Umbraco domain entry to a bare hostname, or null when it names no host.
    /// </summary>
    /// <remarks>
    ///     Umbraco stores these in whatever shape an editor typed: a full URL
    ///     (<c>https://example.com:44366</c>), a bare hostname, a hostname with a port, a wildcard
    ///     (<c>*.example.com</c>), or a path-only culture prefix (<c>/en</c>). Only the first four
    ///     describe a host; a path domain names none and is skipped. Ports are always dropped, because
    ///     a report's <c>url</c> is compared on host alone.
    /// </remarks>
    public static string? NormaliseDomainToHost(string? domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName))
        {
            return null;
        }

        string value = domainName.Trim();

        if (value.StartsWith('/'))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute) && absolute.Host.Length > 0)
        {
            return absolute.Host;
        }

        // Not absolute, so it is a bare host - possibly with a port or a leading wildcard. Parsing it
        // through Uri with a scheme is what strips the port correctly, IPv6 literals included.
        string withoutWildcard = value.TrimStart('*', '.');
        if (withoutWildcard.Length == 0)
        {
            return null;
        }

        return Uri.TryCreate($"https://{withoutWildcard}", UriKind.Absolute, out Uri? implied) && implied.Host.Length > 0
            ? implied.Host
            : withoutWildcard;
    }
}
