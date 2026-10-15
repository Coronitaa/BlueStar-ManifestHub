using System.Net;
using System.Text.Json;
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

    #region ManifestHub2 Tests

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
    public async Task ManifestHub2SourceAdapter_DiscoversManifests_FromFlatDepotMap()
    {
        string flatMapJson = """
        {
            "105601": [ "888888888888888" ],
            "105602": "999999999999999"
        }
        """;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("105600.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(flatMapJson)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        using var adapter = new ManifestHub2SourceAdapter(null, client);

        var manifests = await adapter.DiscoverManifestsAsync(105600);
        Assert.Equal(2, manifests.Count);
        Assert.Contains(manifests, m => m.DepotId == 105601 && m.ManifestId == 888888888888888UL);
        Assert.Contains(manifests, m => m.DepotId == 105602 && m.ManifestId == 999999999999999UL);
    }

    [Fact]
    public async Task ManifestHub2SourceAdapter_HandlesNotFound_Gracefully()
    {
        var handler = new MockHttpMessageHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        using var adapter = new ManifestHub2SourceAdapter(null, client);

        var discovered = await adapter.DiscoverManifestsAsync(999999);
        Assert.Empty(discovered);

        var downloaded = await adapter.DownloadManifestAsync(111, 222);
        Assert.Null(downloaded);
    }

    #endregion

    #region K25 Tests

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
    public async Task K25SourceAdapter_DiscoversManifestsForDepot_FromJson()
    {
        string depotJson = """
        {
            "depotId": 571,
            "manifests": [
                { "manifestId": 12345, "sha256": "deadbeef" },
                { "manifestId": 67890 }
            ]
        }
        """;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("depots/571.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(depotJson)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        using var adapter = new K25SourceAdapter(null, client);

        var discovered = await adapter.DiscoverManifestsForDepotAsync(571);
        Assert.Equal(2, discovered.Count);
        Assert.Equal(12345UL, discovered[0].ManifestId);
        Assert.Equal(571u, discovered[0].DepotId);
        Assert.Equal("deadbeef", discovered[0].ExpectedSha256);
        Assert.Equal(67890UL, discovered[1].ManifestId);
    }

    [Fact]
    public async Task K25SourceAdapter_DiscoversManifests_FromArrayFormat()
    {
        string arrayJson = """
        [
            { "manifestId": 111, "depotId": 731 },
            { "manifestId": 222, "depotId": 731 }
        ]
        """;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(arrayJson)
            };
        });

        using var client = new HttpClient(handler);
        using var adapter = new K25SourceAdapter(null, client);

        var manifests = await adapter.DiscoverManifestsAsync(730);
        Assert.Equal(2, manifests.Count);
        Assert.Contains(manifests, m => m.ManifestId == 111 && m.DepotId == 731);
        Assert.Contains(manifests, m => m.ManifestId == 222 && m.DepotId == 731);
    }

    #endregion

    #region DepotBox Tests

    [Fact]
    public async Task DepotBoxSourceAdapter_WhenBulkNotAllowed_SkipsBulkDiscovery()
    {
        int requestsMade = 0;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            requestsMade++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        });

        using var client = new HttpClient(handler);
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = Path.Combine(_testDir, "gov_skip_bulk.json")
        });

        using var adapter = new DepotBoxSourceAdapter(
            governor,
            new DepotBoxOptions
            {
                AllowBulkIngestion = false // Default
            },
            client);

        var results = await adapter.DiscoverManifestsAsync(730);

        Assert.Empty(results);
        Assert.Equal(0, requestsMade); // Protected: no outbound HTTP request made
    }

    [Fact]
    public async Task DepotBoxSourceAdapter_WhenBulkAllowed_DiscoversManifests()
    {
        string json = """
        {
            "manifests": [
                {
                    "manifestId": 777888999,
                    "depotId": 731,
                    "appId": 730,
                    "sha256": "0123456789abcdef",
                    "creationTime": "2026-01-01T00:00:00Z"
                }
            ]
        }
        """;

        string? requestedPath = null;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            requestedPath = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });

        using var client = new HttpClient(handler);
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = Path.Combine(_testDir, "gov_allow_bulk.json")
        });

        using var adapter = new DepotBoxSourceAdapter(
            governor,
            new DepotBoxOptions
            {
                AllowBulkIngestion = true
            },
            client);

        var results = await adapter.DiscoverManifestsAsync(730);

        Assert.Single(results);
        Assert.Equal(777888999UL, results[0].ManifestId);
        Assert.Equal(731u, results[0].DepotId);
        Assert.Equal(730u, results[0].AppId);
        Assert.Equal("0123456789abcdef", results[0].ExpectedSha256);
        Assert.Equal("/api/apps/730/manifests", requestedPath);
    }

    [Fact]
    public async Task DepotBoxSourceAdapter_DiscoverManifestsForDepot_QueriesCorrectEndpoint()
    {
        string json = """
        [
            { "manifestId": 123456, "depotId": 441, "sha256": "fedcba" }
        ]
        """;

        string? requestedPath = null;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            requestedPath = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });

        using var client = new HttpClient(handler);
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = Path.Combine(_testDir, "gov_depot.json")
        });

        using var adapter = new DepotBoxSourceAdapter(governor, null, client);

        var manifests = await adapter.DiscoverManifestsForDepotAsync(441);

        Assert.Single(manifests);
        Assert.Equal(123456UL, manifests[0].ManifestId);
        Assert.Equal(441u, manifests[0].DepotId);
        Assert.Equal("/api/depots/441/manifests", requestedPath);
    }

    [Fact]
    public async Task DepotBoxSourceAdapter_DownloadManifest_IncludesBearerToken_AndReturnsBytes()
    {
        byte[] expectedPayload = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        string? authHeader = null;
        string? requestedPath = null;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            authHeader = req.Headers.Authorization?.ToString();
            requestedPath = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedPayload)
            };
        });

        using var client = new HttpClient(handler);
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = Path.Combine(_testDir, "gov_download.json")
        });

        using var adapter = new DepotBoxSourceAdapter(
            governor,
            new DepotBoxOptions
            {
                ApiKey = "secret_db_token_123"
            },
            client);

        var bytes = await adapter.DownloadManifestAsync(441, 999111);

        Assert.NotNull(bytes);
        Assert.Equal(expectedPayload, bytes);
        Assert.Equal("Bearer secret_db_token_123", authHeader);
        Assert.Equal("/api/depots/441/manifests/999111", requestedPath);
    }

    [Fact]
    public async Task DepotBoxSourceAdapter_DownloadManifest_404NotFound_DoesNotTripCircuitBreaker()
    {
        var handler = new MockHttpMessageHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = new HttpClient(handler);
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = Path.Combine(_testDir, "gov_404.json")
        });

        using var adapter = new DepotBoxSourceAdapter(
            governor,
            new DepotBoxOptions
            {
                CircuitBreakerFailureThreshold = 2
            },
            client);

        // Multiple 404 lookups should return null and NEVER trip circuit breaker
        for (int i = 0; i < 5; i++)
        {
            var result = await adapter.DownloadManifestAsync(441, (ulong)(100 + i));
            Assert.Null(result);
            Assert.Equal(CircuitBreakerState.Closed, adapter.CircuitState);
            Assert.True(adapter.IsAvailable);
        }
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

    #endregion

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
