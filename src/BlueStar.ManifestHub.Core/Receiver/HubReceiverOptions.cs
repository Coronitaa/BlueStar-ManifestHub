namespace BlueStar.ManifestHub.Core.Receiver;

/// <summary>
/// Configuration options for the Hub Remote Receiver Server.
/// </summary>
/// <remarks>
/// Security Notice:
/// The default host "127.0.0.1" is intended strictly for local development and integration tests.
/// For production remote deployments:
/// - Bind to an appropriate internal or loopback address (e.g., "0.0.0.0" or container internal network IP).
/// - Never expose raw Kestrel directly to the public Internet without TLS termination.
/// - Always deploy behind an authenticated/encrypted HTTPS reverse proxy (e.g. Cloudflare, Nginx, Caddy, or AWS ALB)
///   handling TLS termination, DDoS mitigation, and SSL certificates.
/// </remarks>
public sealed class HubReceiverOptions
{
    public const int DefaultPort = 5088;
    public const string DefaultHost = "127.0.0.1";
    public const long DefaultMaxPayloadSizeBytes = 50L * 1024 * 1024; // 50 MB
    public const int DefaultMaxRequestsPerMinutePerIp = 60;
    public const int DefaultMaxContributionsPerMinutePerContributor = 30;

    /// <summary>
    /// Port to listen on.
    /// </summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Host address to bind to. Default is "127.0.0.1" (local only).
    /// Production deployments require binding appropriately and sitting behind an HTTPS reverse proxy.
    /// </summary>
    public string Host { get; set; } = DefaultHost;

    /// <summary>
    /// Maximum allowed request payload size in bytes. Default is 50 MB (52,428,800 bytes).
    /// Payloads exceeding this threshold will be rejected with HTTP 413 Payload Too Large.
    /// </summary>
    public long MaxPayloadSizeBytes { get; set; } = DefaultMaxPayloadSizeBytes;

    /// <summary>
    /// Maximum allowed requests per minute per client IP address. Default is 60.
    /// </summary>
    public int MaxRequestsPerMinutePerIp { get; set; } = DefaultMaxRequestsPerMinutePerIp;

    /// <summary>
    /// Maximum allowed contributions per minute per Contributor pseudonym. Default is 30.
    /// </summary>
    public int MaxContributionsPerMinutePerContributor { get; set; } = DefaultMaxContributionsPerMinutePerContributor;

    /// <summary>
    /// Optional shared secret key for HMAC-SHA256 signature verification on webhooks.
    /// </summary>
    public string? WebhookSecretKey { get; set; }

    /// <summary>
    /// Whether to enforce webhook HMAC authentication.
    /// </summary>
    public bool RequireWebhookAuthentication { get; set; }

    /// <summary>
    /// Reward points awarded in Stardust ledger per validated manifest contribution. Default is 100.
    /// </summary>
    public int StardustRewardPoints { get; set; } = 100;
}
