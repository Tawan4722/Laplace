namespace Laplace.Core.Enums;

public enum CompressionMethod : byte
{
    Raw = 0,
    Lz4Fast = 1,
    ZstdFast = 2,
    ZstdBalanced = 3,
    ZstdHigh = 4,
    LzmaMax = 5,
    DeflateFallback = 6,
    Blosc2 = 7,
    Zpaq = 8,
    Bsc = 9
}
