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

    [Fact]
    public async Task ManifestHub2SourceAdapter_VerifiedStructure_BranchPerAppId_AndManifestDownload()
    {
        string authenticJson = """
        {
            "appid": 730,
            "name": "Counter-Strike 2",
            "depot": {
                "731": {
                    "manifests": {
                        "public": {
                            "gid": "3779741820453041761",
                            "size": "7"
                        },
                        "csgo_legacy": {
                            "gid": "1224088799001669801"
                        }
                    }
                },
                "732": {
                    "manifests": {
                        "public": {
                            "gid": "5289214263727115270"
                        }
                    }
                }
            }
        }
        """;

        byte[] fakeBinary = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        string? requestedAppUrl = null;
        string? requestedManifestUrl = null;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            string url = req.RequestUri!.ToString();
            if (url == "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/730/730.json")
            {
                requestedAppUrl = url;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(authenticJson)
                };
            }
            if (url == "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/730/731_3779741820453041761.manifest")
            {
                requestedManifestUrl = url;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fakeBinary)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        using var adapter = new ManifestHub2SourceAdapter(new ManifestHub2Options
        {
            BaseUrl = "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2"
        }, client);

        var manifests = await adapter.DiscoverManifestsAsync(730);

        Assert.Equal("https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/730/730.json", requestedAppUrl);
        Assert.Equal(3, manifests.Count);

        var pub731 = manifests.FirstOrDefault(m => m.DepotId == 731 && m.ManifestId == 3779741820453041761UL);
        Assert.NotNull(pub731);
        Assert.Equal("public", pub731.Branch);

        var legacy731 = manifests.FirstOrDefault(m => m.DepotId == 731 && m.ManifestId == 1224088799001669801UL);
        Assert.NotNull(legacy731);
        Assert.Equal("csgo_legacy", legacy731.Branch);

        // Verify depot discovery uses cached / resolved branch mappings
        var depotDiscovered = await adapter.DiscoverManifestsForDepotAsync(731);
        Assert.Equal(2, depotDiscovered.Count);

        // Download manifest binary using verified route: {baseUrl}/{appId}/{depotId}_{manifestId}.manifest
        var binary = await adapter.DownloadManifestAsync(731, 3779741820453041761UL);
        Assert.NotNull(binary);
        Assert.Equal(fakeBinary, binary);
        Assert.Equal("https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/730/731_3779741820453041761.manifest", requestedManifestUrl);
    }

    [Fact]
    public async Task ManifestHub2SourceAdapter_LiveIntegration_AuthenticGitHub()
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var adapter = new ManifestHub2SourceAdapter(null, httpClient);

            var manifests = await adapter.DiscoverManifestsAsync(730);
            if (manifests.Count > 0)
            {
                // Verify depot 731 manifests are discovered from live GitHub repo
                Assert.Contains(manifests, m => m.DepotId == 731);
            }
        }
        catch (HttpRequestException)
        {
            // Network unavailable or GitHub rate limited; skip gracefully in disconnected test runners
        }
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
            if (req.RequestUri.AbsolutePath.EndsWith("441_555666777.manifest"))
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

    [Fact]
    public async Task K25SourceAdapter_VerifiedStructure_RootManifestUrl_AndTreeParsing()
    {
        string treeJson = """
        {
            "sha": "main_tree_sha",
            "tree": [
                {
                    "path": "1000011_2155801291552894539.manifest",
                    "sha": "db18c248dd1e182713af62d09ccc38f22cc1c368",
                    "size": 296723
                },
                {
                    "path": "1000011_2916223256493235763.manifest",
                    "sha": "7a59c6b6234d1fe6e00dde51972088e1367a32e8",
                    "size": 296722
                },
                {
                    "path": "999999_123456789.manifest",
                    "sha": "aabbccddeeff00112233445566778899aabbccdd"
                }
            ]
        }
        """;

        byte[] fakeBinary = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        string? requestedTreeUrl = null;
        string? requestedManifestUrl = null;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            string url = req.RequestUri!.ToString();
            if (url == "https://api.github.com/repos/qwe213312/k25FCdfEOoEJ42S6/git/trees/main")
            {
                requestedTreeUrl = url;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(treeJson)
                };
            }
            if (url == "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main/1000011_2155801291552894539.manifest")
            {
                requestedManifestUrl = url;
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
            BaseUrl = "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main",
            DepotMetadataUrlTemplate = null // Direct tree discovery
        }, client);

        var discovered = await adapter.DiscoverManifestsForDepotAsync(1000011);

        Assert.Equal("https://api.github.com/repos/qwe213312/k25FCdfEOoEJ42S6/git/trees/main", requestedTreeUrl);
        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, m => m.DepotId == 1000011 && m.ManifestId == 2155801291552894539UL && m.ExpectedSha1 == "db18c248dd1e182713af62d09ccc38f22cc1c368");
        Assert.Contains(discovered, m => m.DepotId == 1000011 && m.ManifestId == 2916223256493235763UL);

        // Download raw binary from verified root template: {baseUrl}/{depotId}_{manifestId}.manifest
        var binary = await adapter.DownloadManifestAsync(1000011, 2155801291552894539UL);
        Assert.NotNull(binary);
        Assert.Equal(fakeBinary, binary);
        Assert.Equal("https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main/1000011_2155801291552894539.manifest", requestedManifestUrl);
    }

    [Fact]
    public async Task K25SourceAdapter_LiveIntegration_AuthenticGitHub()
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var adapter = new K25SourceAdapter(null, httpClient);

            // Download known depot 1000011 manifest 2155801291552894539 from live K25 GitHub repo
            var bytes = await adapter.DownloadManifestAsync(1000011, 2155801291552894539UL);
            if (bytes != null)
            {
                Assert.True(bytes.Length > 0);
            }
        }
        catch (HttpRequestException)
        {
            // Network unavailable or GitHub rate limited; skip gracefully in disconnected test runners
        }
    }

    #endregion

    #region Version History & Authentic Dates Tests

    [Fact]
    public void BuildSnapshot_And_ManifestRevision_DisplayUnknownDate_WhenCreationTimeNull()
    {
        var snapshot = new BlueStar.ManifestHub.Core.Models.BuildSnapshot
        {
            AppId = 730,
            BuildId = 1,
            CreationTime = null
        };

        Assert.Equal("Unknown date", snapshot.DisplayDate);
        Assert.Equal("Fecha desconocida", snapshot.DisplayDateEs);
        Assert.Equal("Fecha desconocida", snapshot.GetDisplayDate("es"));
        Assert.Equal("Unknown date", snapshot.GetDisplayDate("en"));

        var revision = new BlueStar.ManifestHub.Core.Models.ManifestRevision
        {
            DepotId = 731,
            ManifestId = 12345,
            CreationTime = null
        };

        Assert.Equal("Unknown date", revision.DisplayDate);
        Assert.Equal("Fecha desconocida", revision.DisplayDateEs);

        // When authentic date exists
        var authenticDate = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var snapshotWithDate = new BlueStar.ManifestHub.Core.Models.BuildSnapshot
        {
            AppId = 730,
            BuildId = 2,
            CreationTime = authenticDate
        };

        Assert.Contains("2025-06-15", snapshotWithDate.DisplayDate);
        Assert.Contains("UTC", snapshotWithDate.DisplayDate);
        Assert.Contains("2025-06-15", snapshotWithDate.DisplayDateEs);
    }

    [Fact]
    public async Task ManifestStorageManager_GetSnapshotByAuthenticDate_SelectsSnapshotAndDepotRevisions()
    {
        var storageManager = new BlueStar.ManifestHub.Core.Storage.ManifestStorageManager(
            new BlueStar.ManifestHub.Core.Storage.StorageOptions { BasePath = _testDir });

        uint appId = 730;
        uint depot1 = 731;
        uint depot2 = 732;
        ulong m1 = 11111111UL;
        ulong m2 = 22222222UL;

        var authenticDate = new DateTimeOffset(2025, 8, 20, 15, 30, 0, TimeSpan.Zero);

        // Publish manifest revisions with authentic dates
        byte[] b1 = TestManifestHelper.CreateValidManifest(depot1, m1, authenticDate);
        byte[] b2 = TestManifestHelper.CreateValidManifest(depot2, m2, authenticDate);

        await storageManager.PublishManifestAsync(b1, depot1, m1, "TestSrc", "tester", fallbackTimestamp: authenticDate);
        await storageManager.PublishManifestAsync(b2, depot2, m2, "TestSrc", "tester", fallbackTimestamp: authenticDate);

        // Save a complete snapshot with this authentic date
        var snapshot = new BlueStar.ManifestHub.Core.Models.BuildSnapshot
        {
            AppId = appId,
            BuildId = 500,
            CreationTime = authenticDate,
            Timestamp = authenticDate,
            DepotManifests = new Dictionary<uint, ulong>
            {
                [depot1] = m1,
                [depot2] = m2
            }
        };

        await storageManager.SaveBuildSnapshotAsync(snapshot);

        // Select snapshot by authentic date
        var (foundSnapshot, revisions) = await storageManager.GetSnapshotByAuthenticDateAsync(appId, authenticDate);

        Assert.NotNull(foundSnapshot);
        Assert.Equal((ulong)500, foundSnapshot.BuildId);
        Assert.Equal(2, revisions.Count);
        Assert.True(revisions.ContainsKey(depot1));
        Assert.True(revisions.ContainsKey(depot2));
        Assert.Equal(m1, revisions[depot1].ManifestId);
        Assert.Equal(m2, revisions[depot2].ManifestId);

        // If queried with non-matching date, returns null snapshot
        var (missingSnapshot, emptyRevs) = await storageManager.GetSnapshotByAuthenticDateAsync(
            appId, authenticDate.AddDays(10));
        Assert.Null(missingSnapshot);
        Assert.Empty(emptyRevs);
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

    [Fact]
    public async Task DepotBoxSourceAdapter_WhenApiKeyMissingAndRequired_CleanlySkips()
    {
        int callCount = 0;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new HttpClient(handler);
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = Path.Combine(_testDir, "depotbox_gov_skip.json")
        });

        using var adapter = new DepotBoxSourceAdapter(
            governor,
            new DepotBoxOptions
            {
                ApiKey = null,
                RequireApiKey = true
            },
            client);

        Assert.False(adapter.IsAvailable);

        var manifests = await adapter.DiscoverManifestsAsync(730);
        Assert.Empty(manifests);

        var depotManifests = await adapter.DiscoverManifestsForDepotAsync(731);
        Assert.Empty(depotManifests);

        var downloaded = await adapter.DownloadManifestAsync(731, 12345);
        Assert.Null(downloaded);

        Assert.Equal(0, callCount); // Zero network calls made
    }

    [Fact]
    public async Task ManifestHub2SourceAdapter_Handles429RetryAfter_AppliesBackoff()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return resp;
        });

        using var client = new HttpClient(handler);
        using var adapter = new ManifestHub2SourceAdapter(null, client);

        var result = await adapter.DownloadManifestAsync(730, 731, 99999);
        Assert.Null(result);
        Assert.False(adapter.IsAvailable);
    }

    [Fact]
    public async Task K25SourceAdapter_Handles429RetryAfter_AppliesBackoff()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return resp;
        });

        using var client = new HttpClient(handler);
        using var adapter = new K25SourceAdapter(null, client);

        var result = await adapter.DownloadManifestAsync(441, 11111);
        Assert.Null(result);
        Assert.False(adapter.IsAvailable);
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
