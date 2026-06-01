# Adaptive Compression (Phase 1)

## Inputs Considered

The adaptive selector uses:

- compression mode (`fast`, `balanced`, `maximum`, `intensive`, `auto`)
- file extension hints
- entropy estimate from sampled bytes
- repetition estimate from sampled bytes

## High-Level Rules

1. Files likely already compressed (media/archive/installer extensions or very high entropy) default to `RAW`.
2. Otherwise, mode selects preferred Zstd profile:
   - `fast` -> prioritize `LZ4_FAST` / `ZSTD_FAST`
   - `balanced` -> prioritize `ZSTD_BALANCED`
   - `maximum` -> prioritize `ZSTD_HIGH`
   - `intensive` -> try the strongest available candidates even for data that looks pre-compressed
   - `auto` -> entropy/repetition/file-type-based candidate set
3. Every block is checked after compression:
   - if `compressed_size >= original_size`, store block as `RAW`.
4. Candidate methods are sample-compressed and scored using mode-weighted ratio/speed/memory/file-type affinity.

This enforces the requirement that Laplace does not blindly grow incompressible data.

## Entropy and Repetition

- Entropy: Shannon entropy over byte frequencies.
- Repetition: fraction of adjacent identical-byte pairs.

Low entropy + high repetition drives stronger compression; high entropy pushes to faster mode or RAW.

## Scoring Direction

Current implementation uses heuristic analysis plus sampled scoring aligned with:

`score = ratio_weight + speed_weight + memory_weight + file_type_weight`

Weights are implemented and can be tuned in code.

## Mode Behavior

- `fast`: prioritize throughput
- `balanced`: default tradeoff
- `maximum`: favor smaller size
- `intensive`: spend more CPU on ratio-focused candidate testing
- `auto`: choose per block/file using content signals

## Advanced Codec Candidates

These algorithms are not currently implemented in Laplace. They are useful reference points for future compression modes because each optimizes a different extreme of the speed/ratio/hardware tradeoff.

### Blosc

Blosc is an in-memory meta-compressor aimed at binary arrays and scientific data. Its main advantage is throughput: for some array-shaped workloads, reading compressed bytes from memory and decompressing them can be faster than moving the larger uncompressed byte stream through memory.

The design splits data into small cache-friendly blocks and combines lightweight codecs with byte/bit shuffling, multithreading, and SIMD-friendly processing. This makes it strongest for numeric arrays and structured binary data where memory bandwidth is the bottleneck, not for general-purpose archival ratios.

Potential Laplace fit: a future `fast`/`auto` candidate for large numeric or database-like binary blocks when decode speed matters more than maximum compression ratio.

### ZPAQ

ZPAQ targets maximum compression ratio through context mixing and probabilistic modeling. Instead of relying mostly on dictionary matches, it models byte/bit context and predicts upcoming data from prior context, which can beat conventional archival codecs on some inputs.

The tradeoff is cost. ZPAQ-style compression is extremely CPU-intensive and slow compared with LZ4, Zstd, Deflate, or normal LZMA workflows. It is best suited to deep cold storage where compressed size matters more than compression time.

Potential Laplace fit: a future `intensive` or dedicated archival mode, likely opt-in only and clearly labeled as slow.

### BSC

BSC is a block-sorting compressor based around Burrows-Wheeler-style transforms, similar in spirit to bzip2 but optimized for larger blocks and parallel execution. Some builds can use NVIDIA CUDA to offload suitable work to the GPU.

Its appeal is high compression ratio with better throughput on large datasets when GPU acceleration is available. Its drawback is portability and deployment complexity: CUDA support depends on hardware, drivers, and native binaries.

Potential Laplace fit: an optional hardware-accelerated backend for large local datasets, not a default method unless CPU fallback and archive compatibility are well-defined.
