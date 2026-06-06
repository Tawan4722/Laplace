using Laplace.Core.Enums;

namespace Laplace.Core.Abstractions;

public interface ICompressorRegistry
{
    IBlockCompressor GetCompressor(CompressionMethod method);
}

public interface IConfigurableCompressorRegistry
{
    IBlockCompressor GetLzmaCompressor(int dictionarySizeBytes, int fastBytes);
}
