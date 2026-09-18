using System;
using System.Buffers.Binary;
using System.IO;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Reads the file version out of a Windows executable's resource section.
/// FileVersionInfo only parses version resources on Windows and returns nulls
/// elsewhere, which would fail every version check on Linux.
/// </summary>
public static class PeFileVersion
{
    private const int DosHeaderLfaNewOffset = 0x3C;
    private const int CoffHeaderSize = 20;
    private const int SectionHeaderSize = 40;

    // VS_FIXEDFILEINFO signature, little-endian.
    private static ReadOnlySpan<byte> FixedFileInfoSignature => [0xBD, 0x04, 0xEF, 0xFE];

    public static string? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt16() != 0x5A4D) // "MZ"
            {
                return null;
            }

            stream.Position = DosHeaderLfaNewOffset;
            var peHeaderOffset = reader.ReadUInt32();
            stream.Position = peHeaderOffset;

            if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
            {
                return null;
            }

            stream.Position = peHeaderOffset + 6;
            var sectionCount = reader.ReadUInt16();
            stream.Position = peHeaderOffset + 20;
            var optionalHeaderSize = reader.ReadUInt16();

            var sectionTable = peHeaderOffset + 4 + CoffHeaderSize + optionalHeaderSize;
            for (var section = 0; section < sectionCount; section++)
            {
                stream.Position = sectionTable + (section * SectionHeaderSize);

                var name = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0');
                stream.Position += 4; // VirtualSize
                stream.Position += 4; // VirtualAddress
                var rawSize = reader.ReadUInt32();
                var rawOffset = reader.ReadUInt32();

                if (name != ".rsrc" || rawSize == 0 || rawOffset == 0 || rawSize > int.MaxValue)
                {
                    continue;
                }

                stream.Position = rawOffset;
                var resources = reader.ReadBytes((int)rawSize);
                return ReadVersion(resources);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }

        return null;
    }

    private static string? ReadVersion(ReadOnlySpan<byte> resources)
    {
        var index = resources.IndexOf(FixedFileInfoSignature);

        // The signature is followed by struct/file version pairs; file version sits at +8.
        if (index < 0 || index + 16 > resources.Length)
        {
            return null;
        }

        var most = BinaryPrimitives.ReadUInt32LittleEndian(resources[(index + 8)..]);
        var least = BinaryPrimitives.ReadUInt32LittleEndian(resources[(index + 12)..]);

        return $"{most >> 16}.{most & 0xFFFF}.{least >> 16}.{least & 0xFFFF}";
    }
}
