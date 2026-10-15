using BlueStar.ManifestHub.Core.Models;

namespace BlueStar.ManifestHub.Core.Ingestion;

/// <summary>
/// Summary result of an ingestion pipeline run.
/// </summary>
public sealed class IngestionBatchSummary
{
    public int TotalDiscovered { get; set; }
    public int AlreadyStored { get; set; }
    public int SuccessfullyIngested { get; set; }
    public int Failed { get; set; }

    public List<ManifestRevision> PublishedRevisions { get; } = new();
    public List<string> Errors { get; } = new();

    public bool HasErrors => Errors.Count > 0;
}
