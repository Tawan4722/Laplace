using Laplace.Core.Enums;

namespace Laplace.Core.Abstractions;

public interface ICompressorRegistry
{
    IBlockCompressor GetCompressor(CompressionMethod method);
    IBlockCompressor GetLzmaCompressor(int dictionarySizeBytes, int fastBytes);
    IBlockCompressor GetZstdCompressor(CompressionMethod method, int level, int windowLog, bool enableLongDistanceMatching);
}
