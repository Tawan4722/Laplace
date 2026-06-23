using System;
using System.Buffers.Binary;

namespace Laplace.Core.Compression;

public static class BcjFilter
{
    public static void EncodeX86(Span<byte> buffer)
    {
        int size = buffer.Length;
        for (int i = 0; i < size - 4; )
        {
            byte b = buffer[i];
            if (b == 0xE8 || b == 0xE9)
            {
                int rel = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(i + 1, 4));
                int dest = rel + i + 5;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(i + 1, 4), dest);
                i += 5;
            }
            else
            {
                i++;
            }
        }
    }

    public static void DecodeX86(Span<byte> buffer)
    {
        int size = buffer.Length;
        for (int i = 0; i < size - 4; )
        {
            byte b = buffer[i];
            if (b == 0xE8 || b == 0xE9)
            {
                int dest = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(i + 1, 4));
                int rel = dest - (i + 5);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(i + 1, 4), rel);
                i += 5;
            }
            else
            {
                i++;
            }
        }
    }
}
