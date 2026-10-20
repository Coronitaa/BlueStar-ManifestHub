using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotCompleteness
{
    Complete,
    PartialDelta,
    Inferred
}
