# Release History

## [Unreleased]

### Features Added

- Added automatic Recroby integration with provider-free startup, a backend configuration switch, an authenticated provider/model catalog, and optional reasoning effort selection.
- Added AI Credit integration with access checks and structured rejection responses, per-request charges and totals grouped by configured provider/model aliases, execution time, logical LLM call counts, and post-settlement user Credit state. Missing or inconsistent route aliases fail accounting. Token statistics are disclosed only to RGF administrators.
- Added authenticated in-process background AI turns with single-use execution identities, per-model Credit checks and settlement, and fresh authorization for each workflow continuation.
