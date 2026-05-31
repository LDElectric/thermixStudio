using System.Buffers.Binary;
using System.IO;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Parser unificado de segmentos FLIR APP1/FFF em JPEGs termográficos.
/// Consolida lógica duplicada de alinhamento, paleta embarcada e imagem visível.
/// </summary>
internal static class FlirFffParser
{
    public static byte[]? TryReadFileBytes(string imagePath)
    {
        try
        {
            return File.ReadAllBytes(imagePath);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? AssembleFff(byte[] bytes)
    {
        var chunks = CollectFlirApp1Chunks(bytes);
        if (chunks.Count == 0)
        {
            return null;
        }

        using var fffStream = new MemoryStream();
        foreach (var chunk in chunks.Values)
        {
            fffStream.Write(chunk, 0, chunk.Length);
        }

        var fff = fffStream.ToArray();
        return HasValidFffSignature(fff) ? fff : null;
    }

    public static void ApplyAlignmentMetadata(string imagePath, RadiometricMetadata metadata)
    {
        var bytes = TryReadFileBytes(imagePath);
        if (bytes is null)
        {
            return;
        }

        var fff = AssembleFff(bytes);
        if (fff is null)
        {
            return;
        }

        ApplyAlignmentMetadataFromFff(fff, metadata);
    }

    public static byte[]? TryExtractEmbeddedPaletteBgra(string imagePath)
    {
        var bytes = TryReadFileBytes(imagePath);
        if (bytes is null)
        {
            return null;
        }

        var fff = AssembleFff(bytes);
        if (fff is null)
        {
            return null;
        }

        return TryExtractEmbeddedPaletteFromFff(fff);
    }

    public static byte[]? TryExtractVisibleJpeg(string imagePath)
    {
        var bytes = TryReadFileBytes(imagePath);
        if (bytes is null)
        {
            return null;
        }

        var fff = AssembleFff(bytes);
        if (fff is not null)
        {
            var fromFff = TryExtractVisibleJpegFromFff(fff);
            if (fromFff is not null)
            {
                return fromFff;
            }
        }

        return TryExtractLargestJpegBySignature(bytes);
    }

    private static SortedDictionary<byte, byte[]> CollectFlirApp1Chunks(byte[] bytes)
    {
        var chunks = new SortedDictionary<byte, byte[]>();
        var index = 0;

        while (index + 4 < bytes.Length)
        {
            if (bytes[index] != 0xFF || bytes[index + 1] != 0xE1)
            {
                index++;
                continue;
            }

            var segmentLength = (bytes[index + 2] << 8) | bytes[index + 3];
            if (segmentLength < 10)
            {
                break;
            }

            var segmentEnd = index + 2 + segmentLength;
            if (segmentEnd > bytes.Length)
            {
                break;
            }

            var contentStart = index + 4;
            if (contentStart + 8 <= bytes.Length &&
                bytes[contentStart] == (byte)'F' &&
                bytes[contentStart + 1] == (byte)'L' &&
                bytes[contentStart + 2] == (byte)'I' &&
                bytes[contentStart + 3] == (byte)'R' &&
                bytes[contentStart + 4] == 0x00)
            {
                var chunkNumber = bytes[contentStart + 6];
                var payloadStart = contentStart + 8;
                var payloadLength = segmentEnd - payloadStart;
                if (payloadLength > 0)
                {
                    var payload = new byte[payloadLength];
                    Buffer.BlockCopy(bytes, payloadStart, payload, 0, payloadLength);
                    chunks[chunkNumber] = payload;
                }
            }

            index = segmentEnd;
        }

        return chunks;
    }

    private static bool HasValidFffSignature(byte[] fff)
    {
        if (fff.Length < 64)
        {
            return false;
        }

        return fff[0] == (byte)'F' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00
            || fff[0] == (byte)'A' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00;
    }

    private static void ApplyAlignmentMetadataFromFff(byte[] fff, RadiometricMetadata metadata)
    {
        var recordDirectoryOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(24, 4));
        var recordCount = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(28, 4));

        if (recordDirectoryOffset <= 0 || recordCount <= 0)
        {
            return;
        }

        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            var entryOffset = recordDirectoryOffset + (recordIndex * 32);
            if (entryOffset + 20 > fff.Length)
            {
                break;
            }

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(fff.AsSpan(entryOffset, 2));
            var recordOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 12, 4));
            var recordLength = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 16, 4));

            if (recordType == 0x0022)
            {
                if (recordOffset >= 0 && recordLength >= 18 && recordOffset + recordLength <= fff.Length)
                {
                    var paletteRecord = fff.AsSpan(recordOffset, recordLength);
                    metadata.PaletteAboveColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 6);
                    metadata.PaletteBelowColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 9);
                    metadata.PaletteOverflowColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 12);
                    metadata.PaletteUnderflowColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 15);
                    metadata.Detector = "FLIR";
                }

                continue;
            }

            if (recordType != 0x002a)
            {
                continue;
            }

            if (recordOffset < 0 || recordLength < 16 || recordOffset + recordLength > fff.Length)
            {
                continue;
            }

            var record = fff.AsSpan(recordOffset, recordLength);
            metadata.Real2IR ??= BitConverter.ToSingle(record[..4]);
            metadata.OffsetX ??= BinaryPrimitives.ReadInt16LittleEndian(record.Slice(4, 2));
            metadata.OffsetY ??= BinaryPrimitives.ReadInt16LittleEndian(record.Slice(6, 2));
            metadata.PiPX1 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(8, 2));
            metadata.PiPX2 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(10, 2));
            metadata.PiPY1 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(12, 2));
            metadata.PiPY2 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(14, 2));
            metadata.Detector = "FLIR";
        }
    }

    private static byte[]? TryExtractEmbeddedPaletteFromFff(byte[] fff)
    {
        var recordDirectoryOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(24, 4));
        var recordCount = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(28, 4));
        if (recordDirectoryOffset <= 0 || recordCount <= 0)
        {
            return null;
        }

        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            var entryOffset = recordDirectoryOffset + (recordIndex * 32);
            if (entryOffset + 20 > fff.Length)
            {
                break;
            }

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(fff.AsSpan(entryOffset, 2));
            if (recordType != 6)
            {
                continue;
            }

            var recordOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 12, 4));
            var recordLength = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 16, 4));

            if (recordOffset <= 0 || recordLength < 256 * 3 || recordOffset + recordLength > fff.Length)
            {
                continue;
            }

            var bgraLut = new byte[256 * 4];
            for (var i = 0; i < 256; i++)
            {
                var yy = fff[recordOffset + i * 3];
                var cr = fff[recordOffset + i * 3 + 1];
                var cb = fff[recordOffset + i * 3 + 2];

                var r = Math.Clamp(yy + 1.402 * (cr - 128), 0, 255);
                var g = Math.Clamp(yy - 0.344 * (cb - 128) - 0.714 * (cr - 128), 0, 255);
                var b = Math.Clamp(yy + 1.772 * (cb - 128), 0, 255);

                bgraLut[i * 4] = (byte)b;
                bgraLut[i * 4 + 1] = (byte)g;
                bgraLut[i * 4 + 2] = (byte)r;
                bgraLut[i * 4 + 3] = 255;
            }

            byte minY = 255;
            byte maxY = 0;
            for (var i = 0; i < 256; i++)
            {
                var v = fff[recordOffset + i * 3];
                if (v < minY)
                {
                    minY = v;
                }

                if (v > maxY)
                {
                    maxY = v;
                }
            }

            if (maxY - minY < 20)
            {
                continue;
            }

            return bgraLut;
        }

        return null;
    }

    private static byte[]? TryExtractVisibleJpegFromFff(byte[] fff)
    {
        var recordDirectoryOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(24, 4));
        var recordCount = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(28, 4));

        if (recordDirectoryOffset <= 0 || recordCount <= 0)
        {
            return null;
        }

        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            var entryOffset = recordDirectoryOffset + (recordIndex * 32);
            if (entryOffset + 20 > fff.Length)
            {
                break;
            }

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(fff.AsSpan(entryOffset, 2));
            if (recordType != 14)
            {
                continue;
            }

            var recordOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 12, 4));
            var recordLength = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 16, 4));
            var jpegOffset = recordOffset + 32;

            if (jpegOffset <= 0 || recordLength <= 0 || jpegOffset + recordLength > fff.Length)
            {
                continue;
            }

            var jpeg = new byte[recordLength];
            Buffer.BlockCopy(fff, jpegOffset, jpeg, 0, recordLength);

            if (jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
            {
                return jpeg;
            }
        }

        return TryExtractLargestJpegBySignature(fff);
    }

    private static byte[]? TryExtractLargestJpegBySignature(ReadOnlySpan<byte> source)
    {
        const byte soi0 = 0xFF;
        const byte soi1 = 0xD8;
        const byte eoi0 = 0xFF;
        const byte eoi1 = 0xD9;

        byte[]? best = null;
        var bestScore = -1;
        var i = 0;

        while (i + 1 < source.Length)
        {
            if (source[i] != soi0 || source[i + 1] != soi1)
            {
                i++;
                continue;
            }

            var start = i;
            i += 2;

            while (i + 1 < source.Length)
            {
                if (source[i] == eoi0 && source[i + 1] == eoi1)
                {
                    var end = i + 2;
                    var length = end - start;
                    if (length > 1024)
                    {
                        var candidate = source.Slice(start, length).ToArray();
                        if (TryGetImageScore(candidate, out var score) && score > bestScore)
                        {
                            best = candidate;
                            bestScore = score;
                        }
                        else if (score == bestScore && best is not null && candidate.Length > best.Length)
                        {
                            best = candidate;
                        }
                    }

                    i = end;
                    break;
                }

                i++;
            }
        }

        return best;
    }

    private static bool TryGetImageScore(byte[] bytes, out int score)
    {
        score = 0;
        try
        {
            using var img = OpenCvSharp.Cv2.ImDecode(bytes, OpenCvSharp.ImreadModes.Color);
            if (img.Empty() || img.Width <= 0 || img.Height <= 0)
            {
                return false;
            }

            score = img.Width * img.Height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int[]? ReadYCrCbTriplet(ReadOnlySpan<byte> source, int offset)
    {
        if (offset < 0 || offset + 3 > source.Length)
        {
            return null;
        }

        return [(int)source[offset], (int)source[offset + 1], (int)source[offset + 2]];
    }
}
