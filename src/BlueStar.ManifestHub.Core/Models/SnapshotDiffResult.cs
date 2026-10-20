using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepotDiffStatus
{
    Unchanged,
    Modified,
    Added,
    Removed
}

public sealed record DepotDiffItem(
    [property: JsonPropertyName("depotId")] uint DepotId,
    [property: JsonPropertyName("baseManifestId")] ulong? BaseManifestId,
    [property: JsonPropertyName("targetManifestId")] ulong? TargetManifestId,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("status")] DepotDiffStatus Status,
    [property: JsonPropertyName("isAvailableInHub")] bool IsAvailableInHub
);

public sealed class SnapshotDiffResult
{
    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("baseBuildId")]
    public ulong? BaseBuildId { get; init; }

    [JsonPropertyName("targetBuildId")]
    public ulong TargetBuildId { get; init; }

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "public";

    [JsonPropertyName("isCompleteComparison")]
    public bool IsCompleteComparison { get; init; } = true;

    [JsonPropertyName("warningMessage")]
    public string? WarningMessage { get; init; }

    [JsonPropertyName("unchanged")]
    public IReadOnlyList<DepotDiffItem> Unchanged { get; init; } = Array.Empty<DepotDiffItem>();

    [JsonPropertyName("modified")]
    public IReadOnlyList<DepotDiffItem> Modified { get; init; } = Array.Empty<DepotDiffItem>();

    [JsonPropertyName("added")]
    public IReadOnlyList<DepotDiffItem> Added { get; init; } = Array.Empty<DepotDiffItem>();

    [JsonPropertyName("removed")]
    public IReadOnlyList<DepotDiffItem> Removed { get; init; } = Array.Empty<DepotDiffItem>();

    [JsonPropertyName("totalDownloadSizeBytes")]
    public long TotalDownloadSizeBytes { get; init; }

    [JsonPropertyName("totalSavedSizeBytes")]
    public long TotalSavedSizeBytes { get; init; }
}
