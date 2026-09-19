using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Our.Umbraco.ErrorDashboard.Configuration;
using Our.Umbraco.ErrorDashboard.Persistence.Dtos;
using Our.Umbraco.ErrorDashboard.Reporting;

namespace Our.Umbraco.ErrorDashboard.Middleware;

/// <summary>
///     Advertises the site's Network Error Logging policy, and collects the reports that policy
///     produces.
/// </summary>
/// <remarks>
///     Both halves live in one middleware because both need to run before anything else in the
///     pipeline. It is registered as Umbraco's <c>PrePipeline</c> filter, which puts it ahead of static
///     files, routing, authentication and antiforgery - so the collector needs none of them, and the
///     headers reach responses Umbraco's own routing never produced, 404s included.
///     <para>
///         Handling the collector here rather than as an MVC controller also sidesteps the fact that
///         nothing in ASP.NET Core's default content negotiation binds <c>application/reports+json</c>.
///     </para>
/// </remarks>
public sealed class NelPolicyMiddleware : IMiddleware
{
    /// <summary>Reporting group name tying the <c>NEL</c> policy to its <c>Report-To</c> endpoint.</summary>
    private const string GroupName = "nel";

    private readonly IReportHostValidator _hostValidator;
    private readonly ILogger<NelPolicyMiddleware> _logger;
    private readonly IOptionsMonitor<ErrorDashboardOptions> _options;
    private readonly INelReportQueue _queue;
    private readonly NelRateLimiter _rateLimiter;

    /// <summary>Cached header values, rebuilt only when configuration or host changes.</summary>
    private volatile CachedHeaders? _cachedHeaders;

    public NelPolicyMiddleware(
        IOptionsMonitor<ErrorDashboardOptions> options,
        INelReportQueue queue,
        IReportHostValidator hostValidator,
        NelRateLimiter rateLimiter,
        ILogger<NelPolicyMiddleware> logger)
    {
        _options = options;
        _queue = queue;
        _hostValidator = hostValidator;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ErrorDashboardOptions options = _options.CurrentValue;

        if (options.Enabled is false)
        {
            await next(context);
            return;
        }

        if (IsCollectorRequest(context, options))
        {
            await HandleCollectorAsync(context, options);
            return;
        }

        AttachPolicyHeaders(context, options);
        await next(context);
    }

    // ---------------------------------------------------------------- policy headers

    /// <summary>
    ///     Queues the policy headers onto the response.
    /// </summary>
    /// <remarks>
    ///     This has to happen in <see cref="HttpResponse.OnStarting(Func{Task})" />. As the outermost
    ///     middleware we do not yet know the status code or content type on the way in, and by the time
    ///     control comes back out the response has already started and the headers are frozen.
    /// </remarks>
    private void AttachPolicyHeaders(HttpContext context, ErrorDashboardOptions options)
    {
        // The backoffice is a private application; instrumenting it would bury real visitor errors
        // under editors' own traffic.
        if (context.Request.Path.StartsWithSegments("/umbraco", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // NEL policies are only stored for secure origins, so on plain HTTP the headers are pure waste.
        if (context.Request.IsHttps is false)
        {
            return;
        }

        context.Response.OnStarting(
            static state =>
            {
                (NelPolicyMiddleware middleware, HttpContext ctx, ErrorDashboardOptions opts) =
                    ((NelPolicyMiddleware, HttpContext, ErrorDashboardOptions))state;
                middleware.WritePolicyHeaders(ctx, opts);
                return Task.CompletedTask;
            },
            (this, context, options));
    }

    private void WritePolicyHeaders(HttpContext context, ErrorDashboardOptions options)
    {
        // The policy is per-origin, not per-response: one HTML document carries it for every
        // subsequent request to the host, assets included. Stamping it on every image and script as
        // well would just be bytes on the wire.
        if (IsHtmlResponse(context.Response) is false)
        {
            return;
        }

        CachedHeaders headers = GetHeaders(context, options);

        context.Response.Headers["Report-To"] = headers.ReportTo;
        context.Response.Headers["NEL"] = headers.Nel;
    }

    private CachedHeaders GetHeaders(HttpContext context, ErrorDashboardOptions options)
    {
        string host = context.Request.Host.Value ?? string.Empty;
        CachedHeaders? cached = _cachedHeaders;

        if (cached is not null && cached.Matches(host, options))
        {
            return cached;
        }

        // The endpoint must be absolute and https, or the browser's first upload is answered with a
        // 307 by UseHttpsRedirection - which sits in front of this middleware - and never retried.
        string endpoint = $"https://{host}{options.ReportPath}";

        string reportTo = JsonSerializer.Serialize(new
        {
            group = GroupName,
            max_age = options.MaxAgeSeconds,
            endpoints = new[] { new { url = endpoint } },
        });

        // NEL is still bound to the legacy Report-To header above; the newer Reporting-Endpoints
        // header does not drive it, so emitting that one instead would silently collect nothing.
        string nel = JsonSerializer.Serialize(new
        {
            report_to = GroupName,
            max_age = options.MaxAgeSeconds,
            include_subdomains = options.IncludeSubdomains,
            failure_fraction = options.FailureFraction,
            success_fraction = options.SuccessFraction,
        });

        cached = new CachedHeaders(
            host,
            options.ReportPath,
            options.MaxAgeSeconds,
            options.IncludeSubdomains,
            options.FailureFraction,
            options.SuccessFraction,
            reportTo,
            nel);

        _cachedHeaders = cached;
        return cached;
    }

    private static bool IsHtmlResponse(HttpResponse response)
    {
        string? contentType = response.ContentType;
        return contentType is not null
               && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- collector

    private static bool IsCollectorRequest(HttpContext context, ErrorDashboardOptions options) =>
        context.Request.Path.Equals(options.ReportPath, StringComparison.OrdinalIgnoreCase);

    private async Task HandleCollectorAsync(HttpContext context, ErrorDashboardOptions options)
    {
        // A subdomain pointing its policy here is cross-origin, and the browser preflights before it
        // will upload. Same-origin is the default and never reaches this branch.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            WriteCorsHeaders(context);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) is false)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers[HeaderNames.Allow] = "POST, OPTIONS";
            return;
        }

        string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_rateLimiter.IsAllowed(clientKey, options.RateLimitPerMinute, DateTime.UtcNow) is false)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        // Reject on the declared length before reading a byte, so an oversized payload costs nothing.
        if (context.Request.ContentLength > options.MaxPayloadBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        byte[]? payload = await ReadCappedBodyAsync(context.Request, options.MaxPayloadBytes, context.RequestAborted);
        if (payload is null)
        {
            // A chunked upload that only revealed its true size while being read.
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        int accepted = Ingest(payload, context, options);

        WriteCorsHeaders(context);
        context.Response.StatusCode = StatusCodes.Status204NoContent;

        if (accepted > 0)
        {
            _logger.LogDebug("Accepted {Count} network error report(s) from {Client}.", accepted, clientKey);
        }
    }

    private int Ingest(byte[] payload, HttpContext context, ErrorDashboardOptions options)
    {
        NelReportEnvelope?[]? envelopes;

        try
        {
            envelopes = JsonSerializer.Deserialize<NelReportEnvelope?[]>(payload);
        }
        catch (JsonException)
        {
            // Malformed input on an unauthenticated endpoint is expected traffic, not an incident.
            // Answer 204 anyway - arguing with a browser's report queue achieves nothing.
            return 0;
        }

        if (envelopes is null)
        {
            return 0;
        }

        string requestHost = context.Request.Host.Host;
        DateTime receivedUtc = DateTime.UtcNow;
        int limit = Math.Min(envelopes.Length, options.MaxReportsPerPayload);
        int accepted = 0;

        bool IsOurs(string reportHost) => _hostValidator.IsAccepted(reportHost, requestHost);

        for (int i = 0; i < limit; i++)
        {
            NelReportDto? row = NelReportParser.TryMap(envelopes[i], options, IsOurs, receivedUtc);
            if (row is null)
            {
                continue;
            }

            _queue.TryEnqueue(row);
            accepted++;
        }

        return accepted;
    }

    /// <summary>
    ///     Reads the body, giving up the moment it exceeds the cap.
    /// </summary>
    /// <returns>The bytes, or null when the cap was passed.</returns>
    private static async Task<byte[]?> ReadCappedBodyAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];

        while (true)
        {
            int read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void WriteCorsHeaders(HttpContext context)
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "content-type";
        context.Response.Headers["Access-Control-Max-Age"] = "86400";
    }

    /// <summary>
    ///     Prebuilt header strings plus the inputs they were built from, so a configuration or host
    ///     change invalidates them but an ordinary request does not re-serialise two JSON documents.
    /// </summary>
    private sealed record CachedHeaders(
        string Host,
        string ReportPath,
        int MaxAgeSeconds,
        bool IncludeSubdomains,
        double FailureFraction,
        double SuccessFraction,
        string ReportTo,
        string Nel)
    {
        public bool Matches(string host, ErrorDashboardOptions options) =>
            Host == host
            && ReportPath == options.ReportPath
            && MaxAgeSeconds == options.MaxAgeSeconds
            && IncludeSubdomains == options.IncludeSubdomains
            && FailureFraction.Equals(options.FailureFraction)
            && SuccessFraction.Equals(options.SuccessFraction);
    }
}
