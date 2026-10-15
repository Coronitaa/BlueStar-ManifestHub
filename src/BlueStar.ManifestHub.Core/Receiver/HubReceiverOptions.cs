namespace BlueStar.ManifestHub.Core.Receiver;

/// <summary>
/// Configuration options for the Hub Remote Receiver Server.
/// </summary>
public sealed class HubReceiverOptions
{
    public const int DefaultPort = 5088;
    public const string DefaultHost = "127.0.0.1";

    public int Port { get; set; } = DefaultPort;
    public string Host { get; set; } = DefaultHost;
    public string? WebhookSecretKey { get; set; }
    public bool RequireWebhookAuthentication { get; set; }
    public int StardustRewardPoints { get; set; } = 100;
}
