using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing an unmet manifest demand for a depot in the Hub.
/// Eligible for Stardust reward accreditation upon successful contribution and validation.
/// </summary>
public sealed class DemandRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }

    [JsonPropertyName("stardustReward")]
    public int StardustReward { get; init; } = 100;

    [JsonPropertyName("detectedAt")]
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Index representing all missing manifests demanded by the Hub.
/// Stored and serialized to indices/demand.json.
/// </summary>
public sealed class DemandIndex
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("totalMissing")]
    public int TotalMissing { get; set; }

    /// <summary>
    /// Index of missing manifests keyed by "appId:depotId:manifestId".
    /// </summary>
    [JsonPropertyName("demands")]
    public Dictionary<string, DemandRecord> Demands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Missing manifests grouped by AppId and DepotId.
    /// </summary>
    [JsonPropertyName("apps")]
    public Dictionary<string, Dictionary<string, List<DemandRecord>>> Apps { get; set; } = new();

    /// <summary>
    /// Flat list of all missing manifest demands.
    /// </summary>
    [JsonPropertyName("items")]
    public List<DemandRecord> Items { get; set; } = new();

    public static DemandIndex FromRecords(IEnumerable<DemandRecord> records)
    {
        var list = records.ToList();
        var demands = new Dictionary<string, DemandRecord>(StringComparer.OrdinalIgnoreCase);
        var apps = new Dictionary<string, Dictionary<string, List<DemandRecord>>>();

        foreach (var r in list)
        {
            string key = $"{r.AppId}:{r.DepotId}:{r.ManifestId}";
            demands[key] = r;

            string appKey = r.AppId.ToString();
            string depotKey = r.DepotId.ToString();

            if (!apps.TryGetValue(appKey, out var depots))
            {
                depots = new Dictionary<string, List<DemandRecord>>();
                apps[appKey] = depots;
            }

            if (!depots.TryGetValue(depotKey, out var manifestList))
            {
                manifestList = new List<DemandRecord>();
                depots[depotKey] = manifestList;
            }

            manifestList.Add(r);
        }

        return new DemandIndex
        {
            LastUpdated = DateTimeOffset.UtcNow,
            TotalMissing = list.Count,
            Demands = demands,
            Apps = apps,
            Items = list
        };
    }

    /// <summary>
    /// Removes a satisfied manifest from the demand index.
    /// </summary>
    public bool Remove(uint depotId, ulong manifestId)
    {
        int removedItems = Items.RemoveAll(d => d.DepotId == depotId && d.ManifestId == manifestId);

        var keysToRemove = Demands
            .Where(kvp => kvp.Value.DepotId == depotId && kvp.Value.ManifestId == manifestId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var k in keysToRemove)
        {
            Demands.Remove(k);
        }

        foreach (var appEntry in Apps.Values)
        {
            string dKey = depotId.ToString();
            if (appEntry.TryGetValue(dKey, out var list))
            {
                list.RemoveAll(d => d.ManifestId == manifestId);
                if (list.Count == 0)
                {
                    appEntry.Remove(dKey);
                }
            }
        }

        TotalMissing = Items.Count;
        LastUpdated = DateTimeOffset.UtcNow;
        return removedItems > 0 || keysToRemove.Count > 0;
    }
}
