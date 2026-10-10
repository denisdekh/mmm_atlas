using System.Buffers.Binary;

namespace MMMAtlas.Voting;

internal static class JpegDimensions
{
    public static bool TryRead(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = height = 0;
        if (content.Length < 4 || content[0] != 0xff || content[1] != 0xd8)
            return false;

        var index = 2;
        while (index + 3 < content.Length)
        {
            if (content[index++] != 0xff) continue;
            while (index < content.Length && content[index] == 0xff) index++;
            if (index >= content.Length) return false;
            var marker = content[index++];
            if (marker is 0xd8 or 0xd9 or 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (index + 1 >= content.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(content[index..]);
            if (length < 2 || index + length > content.Length) return false;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7
                or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 7) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(content[(index + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(content[(index + 5)..]);
                return width > 0 && height > 0;
            }
            if (marker == 0xda) return false;
            index += length;
        }
        return false;
    }
}
