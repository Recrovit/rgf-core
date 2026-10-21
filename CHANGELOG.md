# Release History

## [Unreleased]

### Features Added

- Added Entity-independent conversations in the existing Recroby: authorized catalog search, paginated browsing, detailed Entity/Property metadata, multi-step comparisons and follow-up questions. Previously disclosed metadata is revalidated before subsequent model calls; unverifiable history requires a new conversation.

- Added continuation choices localized from the authenticated user's stored language, with retryable invalid answers that retain the original conversation token.

- Added a server-side Recroby metadata component that validates the selected RGF context's authenticated owner and delegates Entity metadata access, including automatically resolved external fields, to Legacy.
- Added automatic Recroby integration with provider-free startup, a backend configuration switch, an authenticated provider/model catalog, and optional reasoning effort selection.
- Added AI Credit integration with access checks and structured rejection responses, per-request charges and totals grouped by configured provider/model aliases, execution time, logical LLM call counts, and post-settlement user Credit state. Missing or inconsistent route aliases fail accounting. Token statistics are disclosed only to RGF administrators.
- Added authenticated in-process background AI turns with single-use execution identities, per-model Credit checks and settlement, and fresh authorization for each workflow continuation.
