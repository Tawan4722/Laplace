# Ultra Ratio Benchmark

The reproducible benchmark harness is:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\benchmark-ultra-ratio.ps1
```

It generates a deterministic 192 MiB corpus containing:

- a 16 MiB random segment repeated after an 80 MiB gap
- 48 MiB of structured log data
- 32 MiB of high-entropy data

Each archive is integrity-tested. Compression time, archive size, selected method, and peak process working set are written to CSV and JSON under `artifacts/benchmarks/`.

## June 7, 2026 Result

Environment:

- AMD Ryzen 5 5600
- 16 GiB RAM
- Windows 10 build 26200
- .NET SDK 8.0.421
- one compression worker for all ratio-focused tools

| Tool | Archive bytes | Ratio | Time | Peak working set |
| --- | ---: | ---: | ---: | ---: |
| Laplace Extreme, tuned | 134,233,648 | 66.675% | 10.318 s | 739.1 MiB |
| 7-Zip Ultra, LZMA2 128 MiB | 134,237,289 | 66.676% | 81.596 s | 1,352.3 MiB |
| WinRAR Best, RAR5 128 MiB | 134,480,200 | 66.797% | 39.670 s | 1,355.0 MiB |
| Laplace compressed | 151,168,682 | 75.086% | 2.352 s | 1,421.4 MiB |
| Laplace Extreme v1.9.0 baseline | 151,008,032 | 75.007% | 10.673 s | 1,162.7 MiB |

The tuned Extreme path uses Zstd level 15 with a tiered long-distance window. On this corpus it slightly beats the 7-Zip Ultra archive size while using less time and peak memory.

A separate 32 MiB high-entropy test selected RAW, completed in 2.591 seconds, and peaked at 512.3 MiB.

Isolated policy-tier validation on the same corpus measured:

| Available-memory tier | Block | Archive ratio | Peak working set |
| ---: | ---: | ---: | ---: |
| 256 MiB | 16 MiB | 75.003% | 163.1 MiB |
| 512 MiB | 128 MiB | 75.006% | 340.7 MiB |
| 1 GiB | 256 MiB | 66.675% | 738.3 MiB |

Lower tiers avoid a speculative full-block long-distance trial when sampled candidates indicate RAW. This keeps them within budget but can miss repeats farther apart than their block/window sizes.

Managed LZMA remains part of candidate sampling. Large blocks and tiers requiring an LZMA dictionary above 8 MiB use long-distance Zstd when LZMA wins sampling because the managed 128 MiB-dictionary LZMA probe peaked at approximately 2.46 GiB.

These are single-iteration synthetic results intended for regression and tuning. Real-world corpus results can differ, so release decisions should include representative user datasets.
