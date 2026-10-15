namespace BlueStar.ManifestHub.Core.Ingestion;

/// <summary>
/// Configuration options for the ingestion pipeline.
/// </summary>
public sealed class IngestionPipelineOptions
{
    /// <summary>
    /// Maximum number of manifests to process in a single batch before committing indices.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Pseudonym used when attributing automated ingestion publications.
    /// </summary>
    public string DefaultContributorPseudonym { get; set; } = "bluestar-pipeline";

    /// <summary>
    /// If true, stops trying secondary adapters once an adapter successfully downloads and validates a manifest.
    /// </summary>
    public bool StopOnFirstSuccessfulDownload { get; set; } = true;
}
