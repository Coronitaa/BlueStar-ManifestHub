using BlueStar.ManifestHub.Core.Models;

namespace BlueStar.ManifestHub.Core.Storage;

/// <summary>
/// Result of a manifest publication attempt in the storage manager.
/// </summary>
public sealed class PublishResult
{
    public bool IsSuccess { get; init; }
    public bool IsDuplicate { get; init; }
    public ManifestStatus Status { get; init; }
    public string? Message { get; init; }
    public ManifestRevision? Revision { get; init; }
    public string? ManifestPath { get; init; }

    public static PublishResult Success(ManifestRevision revision, string manifestPath, bool isDuplicate = false, string? message = null) =>
        new()
        {
            IsSuccess = true,
            IsDuplicate = isDuplicate,
            Status = revision.Status,
            Revision = revision,
            ManifestPath = manifestPath,
            Message = message ?? (isDuplicate ? "Manifest already present; deduplicated idempotently." : "Manifest published successfully.")
        };

    public static PublishResult Failure(ManifestStatus status, string message) =>
        new()
        {
            IsSuccess = false,
            IsDuplicate = false,
            Status = status,
            Message = message
        };
}
