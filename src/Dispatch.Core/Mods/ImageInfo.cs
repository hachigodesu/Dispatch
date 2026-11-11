using System.Buffers.Binary;

namespace Dispatch.Core.Mods;

public static class ImageInfo
{
    /// <summary>Reads width/height from a JPEG's SOF marker without decoding the image.</summary>
    public static (int Width, int Height)? TryGetJpegSize(ReadOnlySpan<byte> jpg)
    {
        if (jpg.Length < 4 || jpg[0] != 0xFF || jpg[1] != 0xD8)
        {
            return null;
        }

        int pos = 2;
        while (pos + 9 < jpg.Length)
        {
            if (jpg[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            byte marker = jpg[pos + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) or 0xFF)
            {
                pos += marker == 0xFF ? 1 : 2;
                continue;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpg[(pos + 2)..]);
            bool isSof = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
            {
                int height = BinaryPrimitives.ReadUInt16BigEndian(jpg[(pos + 5)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(jpg[(pos + 7)..]);
                return (width, height);
            }

            pos += 2 + segmentLength;
        }

        return null;
    }
}
