using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Serialization;

/// <summary>
/// Centralized JSON serializer options for Manifest Hub models and indices.
/// </summary>
public static class HubJsonSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static string SerializeCompact<T>(T value) =>
        JsonSerializer.Serialize(value, CompactOptions);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    public static async Task SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) =>
        await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);

    public static async ValueTask<T?> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default) =>
        await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
}
