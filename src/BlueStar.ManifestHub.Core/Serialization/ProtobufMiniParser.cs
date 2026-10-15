using System.Buffers.Binary;

namespace BlueStar.ManifestHub.Core.Serialization;

/// <summary>
/// Parsed metadata from a Steam ContentManifestMetadata protobuf message.
/// </summary>
public sealed class ParsedManifestMetadata
{
    public uint DepotId { get; set; }
    public ulong ManifestGid { get; set; }
    public uint CreationTimeUnix { get; set; }
    public bool FilenamesEncrypted { get; set; }
    public ulong DiskOriginalSize { get; set; }
    public ulong DiskCompressedSize { get; set; }
    public uint UniqueChunks { get; set; }
}

/// <summary>
/// Lightweight, zero-dependency parser and writer for Protobuf wire format and Steam manifest structures.
/// </summary>
public static class ProtobufMiniParser
{
    public const uint MagicPayload = 0x71F617D0;     // 1911953360
    public const uint MagicMetadata = 0x1F4812BE;    // 524817086
    public const uint MagicSignature = 0x1B846177;   // 461486103
    public const uint MagicEndOfManifest = 0x32C41BEB; // 851711403

    /// <summary>
    /// Reads an unsigned varint from a span at the given offset.
    /// Returns true if successful, updating offset and value.
    /// </summary>
    public static bool TryReadVarint(ReadOnlySpan<byte> span, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (offset < span.Length && shift < 64)
        {
            byte b = span[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
        }
        return false;
    }

    /// <summary>
    /// Writes an unsigned varint to a stream.
    /// </summary>
    public static void WriteVarint(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        int count = 0;
        while (value >= 0x80)
        {
            buffer[count++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        buffer[count++] = (byte)(value & 0x7F);
        stream.Write(buffer[..count]);
    }

    /// <summary>
    /// Writes a varint-encoded field header (field_number << 3 | wire_type).
    /// </summary>
    public static void WriteTag(Stream stream, int fieldNumber, int wireType)
    {
        ulong tag = ((ulong)(uint)fieldNumber << 3) | (ulong)((uint)wireType & 0x07);
        WriteVarint(stream, tag);
    }

    /// <summary>
    /// Parses ContentManifestMetadata protobuf bytes.
    /// </summary>
    public static ParsedManifestMetadata ParseMetadata(ReadOnlySpan<byte> data)
    {
        var meta = new ParsedManifestMetadata();
        int offset = 0;

        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out ulong tagVal))
                break;

            int fieldNumber = (int)(tagVal >> 3);
            int wireType = (int)(tagVal & 0x07);

            switch (wireType)
            {
                case 0: // Varint
                    if (!TryReadVarint(data, ref offset, out ulong varintVal))
                        return meta;
                    switch (fieldNumber)
                    {
                        case 1: meta.DepotId = (uint)varintVal; break;
                        case 2: meta.ManifestGid = varintVal; break;
                        case 3: meta.CreationTimeUnix = (uint)varintVal; break;
                        case 4: meta.FilenamesEncrypted = varintVal != 0; break;
                        case 5: meta.DiskOriginalSize = varintVal; break;
                        case 6: meta.DiskCompressedSize = varintVal; break;
                        case 7: meta.UniqueChunks = (uint)varintVal; break;
                    }
                    break;

                case 1: // 64-bit fixed
                    if (offset + 8 > data.Length) return meta;
                    offset += 8;
                    break;

                case 2: // Length-delimited (string / bytes / submessage)
                    if (!TryReadVarint(data, ref offset, out ulong lenVal))
                        return meta;
                    if (lenVal > (ulong)(data.Length - offset))
                        return meta;
                    offset += (int)lenVal;
                    break;

                case 5: // 32-bit fixed
                    if (offset + 4 > data.Length) return meta;
                    offset += 4;
                    break;

                default:
                    // Unknown wire type, cannot proceed safely
                    return meta;
            }
        }

        return meta;
    }

    /// <summary>
    /// Serializes ContentManifestMetadata into protobuf bytes.
    /// </summary>
    public static byte[] SerializeMetadata(ParsedManifestMetadata metadata)
    {
        using var ms = new MemoryStream();

        // Field 1: depot_id (varint)
        if (metadata.DepotId != 0)
        {
            WriteTag(ms, 1, 0);
            WriteVarint(ms, metadata.DepotId);
        }

        // Field 2: gid_manifest (varint)
        if (metadata.ManifestGid != 0)
        {
            WriteTag(ms, 2, 0);
            WriteVarint(ms, metadata.ManifestGid);
        }

        // Field 3: creation_time (varint)
        if (metadata.CreationTimeUnix != 0)
        {
            WriteTag(ms, 3, 0);
            WriteVarint(ms, metadata.CreationTimeUnix);
        }

        // Field 4: filenames_encrypted (varint)
        if (metadata.FilenamesEncrypted)
        {
            WriteTag(ms, 4, 0);
            WriteVarint(ms, 1);
        }

        // Field 5: cb_disk_original (varint)
        if (metadata.DiskOriginalSize != 0)
        {
            WriteTag(ms, 5, 0);
            WriteVarint(ms, metadata.DiskOriginalSize);
        }

        // Field 6: cb_disk_compressed (varint)
        if (metadata.DiskCompressedSize != 0)
        {
            WriteTag(ms, 6, 0);
            WriteVarint(ms, metadata.DiskCompressedSize);
        }

        // Field 7: unique_chunks (varint)
        if (metadata.UniqueChunks != 0)
        {
            WriteTag(ms, 7, 0);
            WriteVarint(ms, metadata.UniqueChunks);
        }

        return ms.ToArray();
    }
}
