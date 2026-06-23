using Xunit;
using System;
using System.IO;
using System.Text;
using Laplace.Core.Compression;
using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Services;
using Laplace.Compression;

namespace Laplace.Tests;

public class BcjFilterTests
{
    [Fact]
    public void BcjFilter_X86_RoundTrip_MatchesExactly()
    {
        // Arrange
        var random = new Random(42);
        var original = new byte[1000];
        random.NextBytes(original);

        // Put some realistic x86 E8/E9 jumps in
        original[10] = 0xE8; // CALL
        original[11] = 0x10;
        original[12] = 0x20;
        original[13] = 0x30;
        original[14] = 0x40;

        original[100] = 0xE9; // JMP
        original[101] = 0x50;
        original[102] = 0x60;
        original[103] = 0x70;
        original[104] = 0x80;

        var buffer = new byte[original.Length];
        Array.Copy(original, buffer, original.Length);

        // Act - Encode
        BcjFilter.EncodeX86(buffer);

        // Verify that the bytes actually changed (at least the address bytes should have offset added)
        Assert.NotEqual(original, buffer);

        // Act - Decode
        BcjFilter.DecodeX86(buffer);

        // Assert
        Assert.Equal(original, buffer);
    }

    [Fact]
    public void BcjFilter_ImprovesCompressionRatio()
    {
        // Arrange
        // Create an x86-like instruction stream where multiple CALL (E8) targets the same absolute address.
        // If we use BCJ, they will all map to the same absolute offset, creating massive redundancy.
        // If we don't, they will have different relative offsets, which compress poorly.
        var size = 64 * 1024;
        var original = new byte[size];
        
        // Populate with fake CALLs (0xE8) to the same target address (e.g. 0x5000) from various positions.
        var target = 0x5000;
        for (int i = 0; i < size - 5; i += 16)
        {
            original[i] = 0xE8;
            // relOffset = target - (i + 5)
            int rel = target - (i + 5);
            byte[] relBytes = BitConverter.GetBytes(rel);
            Array.Copy(relBytes, 0, original, i + 1, 4);
        }

        var withoutBcj = new byte[original.Length];
        Array.Copy(original, withoutBcj, original.Length);

        var withBcj = new byte[original.Length];
        Array.Copy(original, withBcj, original.Length);

        // Act
        BcjFilter.EncodeX86(withBcj);

        // Verify that in withBcj, all call targets are identical!
        // First target at i = 0 should be 0x5000
        int targetVal1 = BitConverter.ToInt32(withBcj, 1);
        // Second target at i = 16 should be 0x5000
        int targetVal2 = BitConverter.ToInt32(withBcj, 17);
        Assert.Equal(targetVal1, targetVal2);

        // Now compress both with Zstd or LZMA
        var registry = new CompressorRegistry();
        var compressor = registry.GetCompressor(CompressionMethod.ZstdBalanced);

        var compressedWithoutBcj = compressor.Compress(withoutBcj);
        var compressedWithBcj = compressor.Compress(withBcj);

        // Assert that the compressed size WITH BCJ is significantly smaller
        Assert.True(compressedWithBcj.Length < compressedWithoutBcj.Length,
            $"BCJ size ({compressedWithBcj.Length}) should be smaller than non-BCJ size ({compressedWithoutBcj.Length})");
    }
}
