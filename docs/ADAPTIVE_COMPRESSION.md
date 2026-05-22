# Adaptive Compression (Phase 1)

## Inputs Considered

The adaptive selector uses:

- compression mode (`fast`, `balanced`, `maximum`, `auto`)
- file extension hints
- entropy estimate from sampled bytes
- repetition estimate from sampled bytes

## High-Level Rules

1. Files likely already compressed (media/archive/installer extensions or very high entropy) default to `RAW`.
2. Otherwise, mode selects preferred Zstd profile:
   - `fast` -> prioritize `LZ4_FAST` / `ZSTD_FAST`
   - `balanced` -> prioritize `ZSTD_BALANCED`
   - `maximum` -> prioritize `ZSTD_HIGH`
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
- `auto`: choose per block/file using content signals
