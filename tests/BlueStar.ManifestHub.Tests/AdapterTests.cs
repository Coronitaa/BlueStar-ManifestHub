using System.Net;
using BlueStar.ManifestHub.Core.Ingestion;
using BlueStar.ManifestHub.Core.Ingestion.Adapters;

namespace BlueStar.ManifestHub.Tests;

public sealed class AdapterTests : IDisposable
{
    private readonly string _testDir;

    public AdapterTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("adapter_tests_");
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task ManifestHub2SourceAdapter_DiscoversManifests_FromJson()
    {
        string sampleJson = """
        {
            "appId": 730,
            "name": "Counter-Strike 2",
            "depots": {
                "731": {
                    "manifests": [ "111111111111111", "222222222222222" ],
                    "latestManifestId": "222222222222222"
                },
                "732": [ "333333333333333" ]
            }
        }
        """;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("730.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sampleJson)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        using var adapter = new ManifestHub2SourceAdapter(new ManifestHub2Options
        {
            BaseUrl = "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main"
        }, client);

        var manifests = await adapter.DiscoverManifestsAsync(730);

        Assert.Equal(3, manifests.Count);
        Assert.Contains(manifests, m => m.DepotId == 731 && m.ManifestId == 111111111111111UL);
        Assert.Contains(manifests, m => m.DepotId == 731 && m.ManifestId == 222222222222222UL);
        Assert.Contains(manifests, m => m.DepotId == 732 && m.ManifestId == 333333333333333UL);
    }

    [Fact]
    public async Task K25SourceAdapter_DiscoversManifests_AndHandlesDownload()
    {
        string sampleJson = """
        {
            "appId": 440,
            "manifests": [
                { "manifestId": 555666777, "depotId": 441, "sha256": "abcdef123456" }
            ]
        }
        """;

        byte[] fakeBinary = new byte[] { 0x1, 0x2, 0x3, 0x4 };

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("440.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sampleJson)
                };
            }
            if (req.RequestUri.AbsolutePath.Contains("manifests/441/555666777.manifest"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fakeBinary)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        using var adapter = new K25SourceAdapter(new K25Options
        {
            BaseUrl = "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main"
        }, client);

        var discovered = await adapter.DiscoverManifestsAsync(440);
        Assert.Single(discovered);
        Assert.Equal(441u, discovered[0].DepotId);
        Assert.Equal(555666777UL, discovered[0].ManifestId);
        Assert.Equal("abcdef123456", discovered[0].ExpectedSha256);

        var bytes = await adapter.DownloadManifestAsync(441, 555666777UL);
        Assert.NotNull(bytes);
        Assert.Equal(fakeBinary, bytes);
    }

    [Fact]
    public async Task DepotBoxSourceAdapter_CircuitBreakerTrips_AfterThresholdFailures()
    {
        int callCount = 0;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        using var client = new HttpClient(handler);
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = Path.Combine(_testDir, "depotbox_gov.json")
        });

        using var adapter = new DepotBoxSourceAdapter(
            governor,
            new DepotBoxOptions
            {
                BaseUrl = "https://api.depotbox.org",
                MaxRetries = 1,
                CircuitBreakerFailureThreshold = 3,
                CircuitBreakerOpenDuration = TimeSpan.FromSeconds(60)
            },
            client);

        Assert.Equal(CircuitBreakerState.Closed, adapter.CircuitState);
        Assert.True(adapter.IsAvailable);

        // Fail 1
        await adapter.DownloadManifestAsync(440, 101);
        Assert.Equal(CircuitBreakerState.Closed, adapter.CircuitState);

        // Fail 2
        await adapter.DownloadManifestAsync(440, 102);
        Assert.Equal(CircuitBreakerState.Closed, adapter.CircuitState);

        // Fail 3 -> Trips to Open
        await adapter.DownloadManifestAsync(440, 103);
        Assert.Equal(CircuitBreakerState.Open, adapter.CircuitState);
        Assert.False(adapter.IsAvailable);

        int callCountBefore = callCount;
        // Subsequent calls while Open must NOT call outbound HTTP
        var result = await adapter.DownloadManifestAsync(440, 104);
        Assert.Null(result);
        Assert.Equal(callCountBefore, callCount); // no new HTTP requests made
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
