# Adaptive Compression (Phase 1)

## Inputs Considered

The adaptive selector uses:

- compression mode (`fast`, `balanced`, `maximum`, `intensive`, `compressed`, `extreme`, `auto`)
- file extension hints
- entropy estimate from sampled bytes
- repetition estimate from sampled bytes

## High-Level Rules

1. Files likely already compressed (media/archive/installer extensions or very high entropy) default to `RAW`.
2. Otherwise, mode selects preferred Zstd profile:
   - `fast` -> prioritize `BLOSC2` / `LZ4_FAST` / `ZSTD_FAST`
   - `balanced` -> prioritize `ZSTD_BALANCED` and `BLOSC2`
   - `maximum` -> prioritize `BSC` when configured, then `LZMA_MAX` / `ZSTD_HIGH`
   - `intensive` -> try the strongest available candidates, including configured `ZPAQ` / `BSC`, even for data that looks pre-compressed
   - `compressed` -> strongest ratio-first profile; for `.7z` output, prefer installed 7-Zip solid LZMA2; for `.rar`, prefer installed WinRAR/RAR with RAR5 solid best compression
   - `extreme` -> LPC-only, single-worker large-block compression with an automatically selected 32-128 MiB LZMA dictionary and strict ratio-first scoring
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
- `compressed`: strongest available ratio-first profile
- `extreme`: maximum practical native LPC ratio, using up to approximately 1 GiB of compression memory
- `auto`: choose per block/file using content signals

## Advanced Codec Candidates

Laplace now exposes method IDs for these advanced backends. Blosc2 is built in through a native package. ZPAQ and BSC are optional external-command backends because there is no managed in-tree implementation in the project; they are registered only when both command templates are configured in the environment.

### Blosc: Effective via Cache Optimization

Traditional archival codecs focus mostly on mathematical size reduction. Blosc is more hardware-oriented: it is designed around moving less data through the memory hierarchy and keeping work inside CPU caches.

Blosc splits input into small cache-friendly chunks and can apply shuffle filters before compression. The shuffle step rearranges bytes of the same position/type next to each other, which is especially effective for numeric arrays and structured binary records. Combined with lightweight codecs, SIMD-friendly loops, and multithreading, this can make decompression faster than copying the equivalent uncompressed data through memory.

Laplace fit: `BLOSC2` is a built-in `fast`, `balanced`, and `auto` candidate for large binary arrays, scientific datasets, telemetry streams, and database-like blocks where memory bandwidth and decode speed matter more than maximum archival ratio.

### ZPAQ: Effective via Context Mixing

Standard dictionary compressors such as ZIP-style Deflate, LZ4, Zstd, and many 7z workflows primarily search for repeated byte sequences within a window or dictionary. ZPAQ comes from the PAQ family and uses context mixing: multiple prediction models estimate the probability of the next bit, then their weights are adjusted based on which models have been accurate so far.

That lets ZPAQ capture statistical structure that is not limited to recent exact string repeats. The tradeoff is cost: this style of modeling can be extremely CPU-intensive and much slower than LZ4, Zstd, Deflate, or normal LZMA workflows.

Laplace fit: `ZPAQ` is an optional `intensive`/`compressed` candidate for cases where compressed size matters more than compression time. It is enabled by setting both `LAPLACE_ZPAQ_COMPRESS_COMMAND` and `LAPLACE_ZPAQ_DECOMPRESS_COMMAND`; each command must read `{input}` and write `{output}`.

### BSC: Effective via Massive Parallelism

BSC is a block-sorting compressor built around Burrows-Wheeler-style transforms. Instead of depending entirely on a sequential sliding dictionary, it transforms blocks into forms that expose repeated contexts and can be processed with more parallelism.

Some BSC builds can use GPU acceleration, which makes it interesting for large datasets where high-ratio compression would otherwise take too long on CPU alone. The practical drawback is deployment complexity: GPU acceleration depends on hardware, drivers, native binaries, and a CPU fallback path.

Laplace fit: `BSC` is an optional `maximum`/`intensive`/`compressed` candidate for large local datasets. It is enabled by setting both `LAPLACE_BSC_COMPRESS_COMMAND` and `LAPLACE_BSC_DECOMPRESS_COMMAND`; each command must read `{input}` and write `{output}`.
