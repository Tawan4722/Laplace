using Laplace.Core.Enums;

namespace Laplace.Core.Abstractions;

public interface ICompressorRegistry
{
    IBlockCompressor GetCompressor(CompressionMethod method);
}
