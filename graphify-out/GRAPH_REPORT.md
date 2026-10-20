# Graph Report - BlueStarMANI  (2026-10-20)

## Corpus Check
- 95 files · ~47,855 words
- Verdict: corpus is large enough that graph structure adds value.
- Unclassified: 46 file(s) not represented in the graph (top: .manifest 23, .sha 19, (none) 3)

## Summary
- 1007 nodes · 2941 edges · 44 communities (35 shown, 9 thin omitted)
- Extraction: 82% EXTRACTED · 18% INFERRED · 0% AMBIGUOUS · INFERRED: 519 edges (avg confidence: 0.85)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `a134a319`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- Task
- DemandIndex
- WebhookProcessor
- DiscoveredManifest
- ManifestRevision
- Fact
- DepotBoxSourceAdapter
- ManifestHub2SourceAdapter
- ApiUsageLogPayload
- .CleanupTestDirectory
- .CreateValidManifest
- FileCoordinatedRateGovernor
- K25SourceAdapter
- HubReceiverServer.cs
- HubReceiverServer
- ReceiverModels.cs
- BlueStar.ManifestHub.Tests.csproj
- StorageOptions
- SnapshotDiffResult
- .AwardPointsForManifestAsync
- SteamQrAuthenticator.cs
- BlueStar.ManifestHub.Core.Models
- BlueStar.ManifestHub.Tests
- BlueStar.ManifestHub.Core.Storage
- ManifestValidationResult
- StardustLedgerManager
- PublicationReceipt
- RateGovernorOptions
- TimeSpan
- CoverageRecord
- DepotChangeEvent
- StardustLedgerEntry
- BlueStar.ManifestHub.Core.Serialization
- SnapshotCompleteness
- SourceRecord
- IDisposable
- IGlobalRateGovernor
- BuildHistoryPage
- DepotRecord
- MockHttpMessageHandler
- CircuitBreakerState
- DateCertainty

## God Nodes (most connected - your core abstractions)
1. `ManifestStorageManager` - 66 edges
2. `DiscoveredManifest` - 49 edges
3. `BuildSnapshot` - 47 edges
4. `RemoteReceiverTests` - 39 edges
5. `ManifestHub2SourceAdapter` - 36 edges
6. `K25SourceAdapter` - 35 edges
7. `DepotBoxSourceAdapter` - 34 edges
8. `FileCoordinatedRateGovernor` - 34 edges
9. `BlueStar.ManifestHub.Core.Models` - 34 edges
10. `WebhookProcessor` - 34 edges

## Surprising Connections (you probably didn't know these)
- `2. Matriz de Requisitos vs. Estado de Componentes (Hito 0 - Auditoría Consolidada)` --references--> `SnapshotCompleteness`  [INFERRED]
  STATUS.md → src/BlueStar.ManifestHub.Core/Models/SnapshotCompleteness.cs
- `RemoteReceiverTests` --calls--> `HubReceiverOptions`  [INFERRED]
  tests/BlueStar.ManifestHub.Tests/RemoteReceiverTests.cs → src/BlueStar.ManifestHub.Core/Receiver/HubReceiverOptions.cs
- `DemandAndDeltaTests` --calls--> `StorageOptions`  [INFERRED]
  tests/BlueStar.ManifestHub.Tests/DemandAndDeltaTests.cs → src/BlueStar.ManifestHub.Core/Storage/StorageOptions.cs
- `HistoryAndDiffApiTests` --calls--> `StorageOptions`  [INFERRED]
  tests/BlueStar.ManifestHub.Tests/HistoryAndDiffApiTests.cs → src/BlueStar.ManifestHub.Core/Storage/StorageOptions.cs
- `IngestionPipelineTests` --calls--> `StorageOptions`  [INFERRED]
  tests/BlueStar.ManifestHub.Tests/IngestionPipelineTests.cs → src/BlueStar.ManifestHub.Core/Storage/StorageOptions.cs

## Import Cycles
- None detected.

## Communities (44 total, 9 thin omitted)

### Community 0 - "Task"
Cohesion: 0.06
Nodes (28): DemandEngine, AppRecord, AppId, DepotIds, LastUpdated, Metadata, Name, SchemaVersion (+20 more)

### Community 1 - "DemandIndex"
Cohesion: 0.06
Nodes (24): LocalLicenseReader, Program, LoginUserEntry, SteamAccountInfo, SteamLocator, HarvestResult, SteamManifestHarvester, SteamAuthToken (+16 more)

### Community 2 - "WebhookProcessor"
Cohesion: 0.07
Nodes (40): DepotManifestPair, DepotId, ManifestId, DepotUpdateItem, DepotId, ManifestId, PreviousManifestId, GameAddedWebhookPayload (+32 more)

### Community 3 - "DiscoveredManifest"
Cohesion: 0.06
Nodes (41): DiscoveredManifest, AppId, Branch, BuildId, CreationTime, DepotId, ExpectedSha1, ExpectedSha256 (+33 more)

### Community 4 - "ManifestRevision"
Cohesion: 0.05
Nodes (41): ContributionRecord, ContributionId, ContributorPseudonym, DepotId, ManifestId, Notes, SchemaVersion, StardustPointsAwarded (+33 more)

### Community 5 - "Fact"
Cohesion: 0.12
Nodes (11): ContributionSubmission, AnonymousContributorId, AppId, AuthenticBuildSnapshot, DepotId, ManifestHashSha256, ManifestId, ManifestSizeBytes (+3 more)

### Community 6 - "DepotBoxSourceAdapter"
Cohesion: 0.10
Nodes (17): DepotBoxOptions, AllowBulkIngestion, ApiKey, BaseUrl, CircuitBreakerFailureThreshold, CircuitBreakerOpenDuration, MaxRetries, Priority (+9 more)

### Community 7 - "ManifestHub2SourceAdapter"
Cohesion: 0.12
Nodes (11): ManifestHub2Options, AppMetadataUrlTemplate, BaseUrl, ManifestUrlTemplate, Priority, Timeout, ManifestHub2SourceAdapter, IsAvailable (+3 more)

### Community 8 - "ApiUsageLogPayload"
Cohesion: 0.09
Nodes (17): DataSanitizer, WebhookLogReporter, WebhookLogReporterOptions, BaseUrl, LogsEndpointPath, Timeout, ApiUsageLogPayload, ClientIp (+9 more)

### Community 9 - ".CleanupTestDirectory"
Cohesion: 0.08
Nodes (9): ParsedManifestMetadata, CreationTimeUnix, DepotId, DiskCompressedSize, DiskOriginalSize, FilenamesEncrypted, ManifestGid, UniqueChunks (+1 more)

### Community 11 - "FileCoordinatedRateGovernor"
Cohesion: 0.19
Nodes (9): FileCoordinatedRateGovernor, MaxRequestsPerMinute, RequestsPerMinute, RateGovernorDiskState, BackoffUntilTicksUtc, Consecutive429Count, LastUpdatedTicksUtc, NextAvailableTicksUtc (+1 more)

### Community 12 - "K25SourceAdapter"
Cohesion: 0.19
Nodes (6): K25SourceAdapter, IsAvailable, Priority, SourceId, SourceType, AdapterTests

### Community 14 - "HubReceiverServer"
Cohesion: 0.10
Nodes (17): HubReceiverOptions, Host, MaxContributionsPerMinutePerContributor, MaxPayloadSizeBytes, MaxRequestsPerMinutePerIp, Port, RequireWebhookAuthentication, StardustRewardPoints (+9 more)

### Community 15 - "ReceiverModels.cs"
Cohesion: 0.09
Nodes (22): BlueStar.ManifestHub.Core.Receiver, ContributorBalanceResponse, ConfirmedBalance, ConfirmedPoints, ContributorId, PendingBalance, PendingPoints, CoverageManifestItem (+14 more)

### Community 16 - "BlueStar.ManifestHub.Tests.csproj"
Cohesion: 0.12
Nodes (14): coverlet.collector (6.0.0), Microsoft.NET.Test.Sdk (17.8.0), QRCoder (1.6.0), SteamKit2 (3.2.0), xunit (2.5.3), xunit.runner.visualstudio (2.5.3), net8.0, Microsoft.NET.Sdk (+6 more)

### Community 17 - "StorageOptions"
Cohesion: 0.11
Nodes (10): StorageOptions, BasePath, BuildsDirectoryName, IndicesDirectoryName, LedgerDirectoryName, ManifestsDirectoryName, SnapshotsDirectoryName, StagingDirectoryName (+2 more)

### Community 18 - "SnapshotDiffResult"
Cohesion: 0.11
Nodes (19): DepotDiffItem, DepotDiffStatus, Added, Modified, Removed, Unchanged, SnapshotDiffResult, Added (+11 more)

### Community 19 - ".AwardPointsForManifestAsync"
Cohesion: 0.20
Nodes (6): DuplicateArtifactRewardException, ArtifactKey, InvalidContributorIdentityException, LedgerException, LedgerValidationException, StardustLedgerTests

### Community 22 - "BlueStar.ManifestHub.Tests"
Cohesion: 0.20
Nodes (3): BlueStar.ManifestHub.Core.Validation, BlueStar.ManifestHub.Core.Ingestion, BlueStar.ManifestHub.Tests

### Community 23 - "BlueStar.ManifestHub.Core.Storage"
Cohesion: 0.21
Nodes (5): BlueStar.ManifestHub.Cli, BlueStar.ManifestHub.Core.Storage, BlueStar.ManifestHub.Core.Ledger, BlueStar.ManifestHub.Core.Demand, Program

### Community 24 - "ManifestValidationResult"
Cohesion: 0.13
Nodes (15): ManifestValidationResult, AuthenticCreationTime, CreationTime, DecompressedPayload, DepotId, ErrorMessage, HasAuthenticCreationTime, IsDecompressed (+7 more)

### Community 25 - "StardustLedgerManager"
Cohesion: 0.19
Nodes (8): LedgerIntegrityResult, EntryCount, ErrorMessage, IsValid, LastBlockHash, StardustLedgerManager, LedgerFilePath, StardustJsonPath

### Community 26 - "PublicationReceipt"
Cohesion: 0.17
Nodes (12): PublicationReceipt, AppId, CryptographicVerificationHash, DepotId, IsDuplicate, IsSuccess, ManifestId, ReceiptId (+4 more)

### Community 27 - "RateGovernorOptions"
Cohesion: 0.18
Nodes (8): RateGovernorOptions, InitialBackoff, JitterRatio, MaxBackoff, MaxBurst, RequestsPerMinute, StateFilePath, TimeProvider

### Community 28 - "TimeSpan"
Cohesion: 0.18
Nodes (8): K25Options, AppMetadataUrlTemplate, BaseUrl, DepotMetadataUrlTemplate, ManifestUrlTemplate, Priority, Timeout, TreeApiUrlTemplate

### Community 29 - "CoverageRecord"
Cohesion: 0.20
Nodes (10): CoverageRecord, AppId, AvailableRevisions, CoverageRatio, DepotId, LastChecked, LatestAvailableManifestId, MissingRevisions (+2 more)

### Community 30 - "DepotChangeEvent"
Cohesion: 0.20
Nodes (10): DepotChangeEvent, AppId, DepotId, DetectedAt, EventId, Metadata, NewManifestId, PreviousManifestId (+2 more)

### Community 31 - "StardustLedgerEntry"
Cohesion: 0.20
Nodes (10): StardustLedgerEntry, ArtifactKey, ContributorPseudonym, EntryHash, Points, PreviousHash, Reason, SchemaVersion (+2 more)

### Community 33 - "SnapshotCompleteness"
Cohesion: 0.22
Nodes (8): SnapshotCompleteness, Complete, Inferred, PartialDelta, 1. Misión y Repositorios, 2. Matriz de Requisitos vs. Estado de Componentes (Hito 0 - Auditoría Consolidada), 3. Plan de Ejecución por Hitos, BlueStar + Manifest Hub: Estado de Integración

### Community 34 - "SourceRecord"
Cohesion: 0.22
Nodes (9): SourceRecord, Config, IsActive, LastSync, Name, Priority, SchemaVersion, SourceId (+1 more)

### Community 36 - "IGlobalRateGovernor"
Cohesion: 0.29
Nodes (4): IGlobalRateGovernor, MaxRequestsPerMinute, RequestsPerMinute, RateGovernorStateSnapshot

### Community 37 - "BuildHistoryPage"
Cohesion: 0.25
Nodes (8): BuildHistoryPage, AppId, Branch, Items, Page, PageSize, TotalItems, TotalPages

### Community 38 - "DepotRecord"
Cohesion: 0.25
Nodes (8): DepotRecord, AppId, DepotId, LastUpdated, LatestManifestId, ManifestIds, Name, SchemaVersion

### Community 40 - "CircuitBreakerState"
Cohesion: 0.50
Nodes (4): CircuitBreakerState, Closed, HalfOpen, Open

### Community 41 - "DateCertainty"
Cohesion: 0.50
Nodes (4): DateCertainty, Estimated, Unknown, Verified

## Knowledge Gaps
- **369 isolated node(s):** `net8.0`, `SteamKit2 (3.2.0)`, `QRCoder (1.6.0)`, `Microsoft.NET.Sdk`, `net8.0` (+364 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 416 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **9 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `DepotBoxSourceAdapter` connect `DepotBoxSourceAdapter` to `Task`, `IDisposable`, `DiscoveredManifest`, `IGlobalRateGovernor`, `ApiUsageLogPayload`, `CircuitBreakerState`, `DateTimeOffset`, `HubReceiverServer.cs`, `TimeSpan`?**
  _High betweenness centrality (0.056) - this node is a cross-community bridge._
- **Are the 5 inferred relationships involving `ManifestStorageManager` (e.g. with `.Main()` and `.ManifestStorageManager_GetSnapshotByAuthenticDate_SelectsSnapshotAndDepotRevisions()`) actually correct?**
  _`ManifestStorageManager` has 5 INFERRED edges - model-reasoned connections that need verification._
- **What connects `net8.0`, `SteamKit2 (3.2.0)`, `QRCoder (1.6.0)` to the rest of the system?**
  _369 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Task` be split into smaller, more focused modules?**
  _Cohesion score 0.06373031496062992 - nodes in this community are weakly interconnected._
- **Why does `BuildSnapshot` connect `Task` to `SnapshotCompleteness`, `ManifestRevision`, `BuildHistoryPage`, `Fact`, `DateCertainty`, `DateTimeOffset`, `BlueStar.ManifestHub.Core.Models`?**
  _High betweenness centrality (0.042) - this node is a cross-community bridge._
- **Are the 10 inferred relationships involving `DiscoveredManifest` (e.g. with `.RegisterManifest()` and `.IngestManifestAsync()`) actually correct?**
  _`DiscoveredManifest` has 10 INFERRED edges - model-reasoned connections that need verification._
- **Should `DemandIndex` be split into smaller, more focused modules?**
  _Cohesion score 0.05507246376811594 - nodes in this community are weakly interconnected._