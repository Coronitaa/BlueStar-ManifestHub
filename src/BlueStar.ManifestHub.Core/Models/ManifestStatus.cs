using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Status of a depot manifest revision in the hub.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManifestStatus
{
    /// <summary>
    /// Manifest exists locally, magic bytes verified, checksum verified, payload intact.
    /// </summary>
    AvailableAndValidated,

    /// <summary>
    /// Manifest has been registered or discovered but payload is awaiting download/verification.
    /// </summary>
    Pending,

    /// <summary>
    /// Manifest is known to exist on Steam history but has not been obtained from any source.
    /// </summary>
    Missing,

    /// <summary>
    /// File payload is damaged, truncated, or failed checksum/magic verification.
    /// </summary>
    Corrupted,

    /// <summary>
    /// Manifest cannot be obtained due to server unreachable or transport failure.
    /// </summary>
    Inaccessible,

    /// <summary>
    /// Manifest requires special credentials/license that the hub does not have access to.
    /// </summary>
    Restricted,

    /// <summary>
    /// Manifest has been superseded by a newer verified revision on the same depot/branch.
    /// </summary>
    Superseded,

    /// <summary>
    /// Manifest payload received from untrusted source but not yet cryptographically verified.
    /// </summary>
    Unverified
}
