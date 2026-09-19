using System.Text.Json.Serialization;

namespace Our.Umbraco.ErrorDashboard.Reporting;

/// <summary>
///     The wire shape the browser POSTs. A payload is a JSON array of these.
/// </summary>
/// <remarks>
///     Snake-case names are the spec's, not a style choice. Everything is nullable because this is
///     unauthenticated input from the internet - the parser decides what is usable, this type only
///     describes what may arrive.
/// </remarks>
public sealed class NelReportEnvelope
{
    /// <summary>Milliseconds between the error happening and the report being delivered.</summary>
    [JsonPropertyName("age")]
    public long Age { get; set; }

    /// <summary>Report family. Only <c>network-error</c> concerns us; the Reporting API carries others.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("body")]
    public NelReportBody? Body { get; set; }
}

/// <summary>The <c>body</c> of a network-error report, per the W3C Network Error Logging spec.</summary>
public sealed class NelReportBody
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary><c>dns</c>, <c>connection</c> or <c>application</c>.</summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("status_code")]
    public int? StatusCode { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("server_ip")]
    public string? ServerIp { get; set; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }

    [JsonPropertyName("elapsed_time")]
    public int? ElapsedTime { get; set; }

    [JsonPropertyName("sampling_fraction")]
    public double? SamplingFraction { get; set; }
}
