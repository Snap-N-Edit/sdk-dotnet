# Changelog

All notable changes to the snapnedit .NET SDK. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-09-13

First release. Targets `netstandard2.0` and `net8.0`; no dependencies beyond
the BCL (`System.Text.Json` on `netstandard2.0`).

### Added

- `SnapneditClient(apiKey, options?)` with an injectable `HttpClient`, a
  configurable base url, per-request timeout, and backoff retries (network,
  `429`, `5xx`) on idempotent requests only.
- `RunAsync` — upload, create, poll and download in one call, including mask
  upload for mask-guided operations and the delivery-aware download default.
- `CreateJobAsync`, `GetJobAsync`, `WaitForJobAsync`, `UploadAsync`,
  `ListOperationsAsync`.
- `JobInput.FromBytes / FromStream / FromFile / FromUrl / FromAssetId`.
- Bring your own storage: `JobDestination.PresignedPut`, `JobDestination.Saved`
  and `JobDestination.OptOut` (an explicit `destination: null`, distinct from
  omitting the field).
- `Destinations` — `ListAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`,
  `TestAsync`, `PresignUploadAsync`.
- `Embed` — `CreateSessionAsync`, `CreateTokenAsync`.
- `Designs` — `CreateAsync`, `CreatePagesAsync`, `RenderAsync`.
- `SnapneditWebhooks` — `Verify`, `ConstructEvent`, `Parse`,
  `ComputeSignature`, with constant-time comparison, multi-`v1` rotation
  support and an optional freshness window.
- `SnapneditException(ErrorCode, string, int)` and `SnapneditTimeoutException`.
- `Operations` constants and credit costs for all 17 operations, plus a typed
  params record per operation.
- Conformance suite: one test per scenario in the monorepo's
  `test/conformance/scenarios.json`, driven against the real api and worker.

[Unreleased]: https://github.com/Snap-N-Edit/sdk-dotnet/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Snap-N-Edit/sdk-dotnet/releases/tag/v0.1.0
