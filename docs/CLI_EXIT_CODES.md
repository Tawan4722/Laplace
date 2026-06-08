# Laplace CLI Exit Codes

Laplace uses stable process exit codes so scripts can distinguish argument errors from archive failures.

| Code | Meaning |
| ---: | --- |
| 0 | Command completed successfully. For `--dry-run`, no mutation was performed. |
| 1 | Usage, argument, unsupported-command, or unsupported-format error. |
| 2 | Runtime operation failure, archive integrity failure, password failure, invalid archive data, or other exception caught by the CLI. |

Use `--json` on supported commands to get machine-readable stdout. Diagnostic errors are still written to stderr.

`view` intentionally writes raw entry bytes to stdout and does not support JSON wrapping.
