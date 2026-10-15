using System.Net;
using BlueStar.ManifestHub.Core.Webhooks;

namespace BlueStar.ManifestHub.Tests;

public sealed class WebhookSanitizationTests
{
    [Fact]
    public void SanitizeText_RemovesAllSteamIdFormats()
    {
        string input = "User 76561198012345678 downloaded with [U:1:12345678] and STEAM_0:1:55443322";
        string sanitized = DataSanitizer.SanitizeText(input);

        Assert.DoesNotContain("76561198012345678", sanitized);
        Assert.DoesNotContain("[U:1:12345678]", sanitized);
        Assert.DoesNotContain("STEAM_0:1:55443322", sanitized);
        Assert.Contains("[REDACTED_STEAMID]", sanitized);
    }

    [Fact]
    public void SanitizeText_RemovesBearerTokensAndJwts()
    {
        string fakeJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozGz6b4x";
        string input = $"Headers: Bearer mySecretToken123456 and token={fakeJwt}";

        string sanitized = DataSanitizer.SanitizeText(input);

        Assert.DoesNotContain("mySecretToken123456", sanitized);
        Assert.DoesNotContain(fakeJwt, sanitized);
        Assert.Contains("Bearer [REDACTED]", sanitized);
    }

    [Fact]
    public void SanitizeText_RemovesPasswordsAndApiKeys()
    {
        string input = "Connection string: password=P@ssw0rd123!; apiKey=db_live_key999; secret: topsecret456";
        string sanitized = DataSanitizer.SanitizeText(input);

        Assert.DoesNotContain("P@ssw0rd123!", sanitized);
        Assert.DoesNotContain("db_live_key999", sanitized);
        Assert.DoesNotContain("topsecret456", sanitized);
        Assert.Contains("password=[REDACTED]", sanitized);
        Assert.Contains("apiKey=[REDACTED]", sanitized);
        Assert.Contains("secret:[REDACTED]", sanitized);
    }

    [Fact]
    public void SanitizeText_RemovesEmailsAndIpAddresses()
    {
        string input = "Contact user admin@depotbox.org from 192.168.1.55 or 10.0.0.1";
        string sanitized = DataSanitizer.SanitizeText(input);

        Assert.DoesNotContain("admin@depotbox.org", sanitized);
        Assert.DoesNotContain("192.168.1.55", sanitized);
        Assert.DoesNotContain("10.0.0.1", sanitized);
        Assert.Contains("[REDACTED_EMAIL]", sanitized);
        Assert.Contains("[REDACTED_IP]", sanitized);
    }

    [Fact]
    public void SanitizeUrl_StripsSensitiveQueryParameters()
    {
        string url = "https://api.depotbox.org/api/depots/440/manifests?api_key=secretKey123&user=john@doe.com&depotId=440";
        string sanitized = DataSanitizer.SanitizeUrl(url);

        Assert.DoesNotContain("secretKey123", sanitized);
        Assert.DoesNotContain("john@doe.com", sanitized);
        Assert.Contains("api_key=[REDACTED]", sanitized);
        Assert.Contains("user=[REDACTED_EMAIL]", sanitized);
        Assert.Contains("depotId=440", sanitized);
    }

    [Fact]
    public void SanitizeDictionary_RedactsSensitiveKeysAndValues()
    {
        var rawDict = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer token-12345-abc",
            ["ApiKey"] = "super-secret-key",
            ["CustomHeader"] = "NormalValue",
            ["Notes"] = "Sent by 76561198000000001 with pass password=secret"
        };

        var sanitized = DataSanitizer.SanitizeDictionary(rawDict);

        Assert.Equal("[REDACTED]", sanitized["Authorization"]);
        Assert.Equal("[REDACTED]", sanitized["ApiKey"]);
        Assert.Equal("NormalValue", sanitized["CustomHeader"]);

        Assert.DoesNotContain("76561198000000001", sanitized["Notes"]);
        Assert.DoesNotContain("secret", sanitized["Notes"]);
        Assert.Contains("[REDACTED_STEAMID]", sanitized["Notes"]);
        Assert.Contains("password=[REDACTED]", sanitized["Notes"]);
    }

    [Fact]
    public async Task WebhookLogReporter_OutboundRequestContainsZeroSecrets()
    {
        string? capturedRequestBody = null;
        var mockHandler = new TestHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Content != null)
            {
                capturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(mockHandler);
        using var reporter = new WebhookLogReporter(
            new WebhookLogReporterOptions { BaseUrl = "https://api.depotbox.org" },
            httpClient);

        var rawLog = new ApiUsageLogPayload
        {
            Endpoint = "/api/apps/730/manifests?token=secretTokenXYZ&steamid=76561198099999999",
            HttpMethod = "POST",
            StatusCode = 200,
            DurationMs = 125,
            ClientIp = "172.16.254.1",
            UserAgent = "BlueStar-Client/1.0 (contact: support@bluestar.hub)",
            Details = new Dictionary<string, string>
            {
                ["api_key"] = "critical_api_key_443",
                ["password"] = "adminPassword123!",
                ["comment"] = "User 76561198099999999 requested manifest"
            }
        };

        bool reported = await reporter.ReportLogAsync(rawLog);

        Assert.True(reported);
        Assert.NotNull(capturedRequestBody);

        // Verify zero leakage of credentials, tokens, passwords, SteamIDs, or emails
        Assert.DoesNotContain("secretTokenXYZ", capturedRequestBody);
        Assert.DoesNotContain("76561198099999999", capturedRequestBody);
        Assert.DoesNotContain("172.16.254.1", capturedRequestBody);
        Assert.DoesNotContain("support@bluestar.hub", capturedRequestBody);
        Assert.DoesNotContain("critical_api_key_443", capturedRequestBody);
        Assert.DoesNotContain("adminPassword123!", capturedRequestBody);

        // Verify redact placeholders are present
        Assert.Contains("[REDACTED]", capturedRequestBody);
        Assert.Contains("[REDACTED_STEAMID]", capturedRequestBody);
        Assert.Contains("[REDACTED_EMAIL]", capturedRequestBody);
        Assert.Contains("[REDACTED_IP]", capturedRequestBody);
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
