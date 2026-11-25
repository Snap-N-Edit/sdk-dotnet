# Snapnedit — the official .NET SDK

A small, dependency-free client for the [snapnedit](https://snapnedit.com) AI
photo-editing API: upload an image, run an operation, get the result — or hand
the API a URL and a destination bucket and never touch the bytes at all.

- Targets **`netstandard2.0`** and **`net8.0`** — .NET Framework 4.6.1+, .NET
  Core, .NET 5–10, Xamarin, Unity, MAUI.
- **No dependencies** beyond the BCL (`System.Text.Json` on `netstandard2.0`).
- **No reflection**: every request is written with `Utf8JsonWriter` and every
  response is read field-by-field off `JsonElement`, so the package is
  trim- and AOT-safe.
- Async-only, with a `CancellationToken` on every call.
- Bring your own `HttpClient`.

```sh
dotnet add package Snapnedit
```

> **NuGet publication is pending.** The package is not on nuget.org yet; until
> it is, reference the project directly (`dotnet add reference
> path/to/src/Snapnedit/Snapnedit.csproj`) or build a local package with
> `dotnet pack -c Release` and add the output directory as a source.

---

## Fifteen lines

```csharp
using Snapnedit;

using var client = new SnapneditClient(Environment.GetEnvironmentVariable("SNAPNEDIT_API_KEY")!);

// Upload, run, poll, download — one call.
var cutout = await client.RunAsync(Operations.RemoveBackground, JobInput.FromFile("cat.jpg"));
await File.WriteAllBytesAsync("cat-cutout.png", cutout.Output!);

// Typed params, so a bad value is a compile error rather than a 400.
var thumb = await client.RunAsync(
    Operations.ResizeImage,
    JobInput.FromFile("cat.jpg"),
    new RunOptions { TypedParams = new ResizeImageParams { Width = 512, Fit = ResizeFit.Cover } });

Console.WriteLine($"{thumb.Output!.Length} bytes of {thumb.Mime}");
```

`RunAsync` is the whole flow. When you want the steps yourself:

```csharp
var asset  = await client.UploadAsync(File.ReadAllBytes("cat.jpg"), "image/jpeg");
var job    = await client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId(asset.AssetId),
                 new CreateJobOptions { TypedParams = new UpscaleParams { Factor = UpscaleFactor.X4 } });
var done   = await client.WaitForJobAsync(job.JobId);   // waits for delivery to settle too
```

Configuration lives on `SnapneditClientOptions`:

```csharp
using var client = new SnapneditClient(apiKey, new SnapneditClientOptions
{
    BaseUrl = "http://localhost:8787",          // default: https://snapnedit.com/api
    HttpClient = httpClientFactory.CreateClient(), // never disposed by the SDK
    MaxRetries = 2,                              // idempotent calls only — see below
    RequestTimeout = TimeSpan.FromSeconds(100),
});
```

**Retries.** Network failures, `429` and `5xx` are retried with exponential,
jittered backoff (honouring `Retry-After`) on **GET, PUT and DELETE only**. A
`POST` is never retried: it may already have created a job and debited credits
before the response was lost.

---

## Bring your own storage

The bytes never have to pass through your process. Give the API a URL to read
from, a destination to write to, or both.

```csharp
// Input: a presigned GET in your own bucket. The SERVER fetches it — https
// only, no redirects, no private addresses, 30s, size-capped. A fetch failure
// is a terminal `input_fetch_failed` and the credits are refunded.
var input = JobInput.FromUrl("https://my-bucket.s3.amazonaws.com/in.png?X-Amz-Signature=…");

// Output: a presigned PUT you signed yourself.
var result = await client.RunAsync(Operations.Upscale, input, new RunOptions
{
    Destination = JobDestination.PresignedPut(
        "https://my-bucket.s3.amazonaws.com/out.png?X-Amz-Signature=…",
        new Dictionary<string, string> { ["content-type"] = "image/png" }),
});

Console.WriteLine(result.Delivery!.Status);   // Delivered
Console.WriteLine(result.Downloaded);         // false — the bytes are already yours
```

When a destination you named was delivered successfully, `RunAsync` skips the
download by default — pulling the bytes back would defeat the point. A delivery
that **failed** still downloads, so you are never left empty-handed
(`status: succeeded` with `delivery.status: failed` is a real and correct
combination; the result is still at `result.Download`). Override either way
with `RunOptions.Download`.

### Saved destinations

Save an S3-compatible bucket once and name it by id — or make it your account
default and stop naming it at all.

```csharp
var bucket = await client.Destinations.CreateAsync(new StorageDestinationInput(
    name: "prod results",
    provider: StorageProvider.CloudflareR2,
    bucket: "results",
    accessKeyId: "…",
    secretAccessKey: "…")           // sealed at rest; no endpoint ever echoes it
{
    AccountId = "…",
    KeyPrefix = "snapnedit/",
    IsDefault = true,               // applied to every job that names no destination
    DeleteAfterDelivery = false,    // true drops our copy once your bucket confirms
});

var probe = await client.Destinations.TestAsync(bucket.Id);   // a real probe write
Console.WriteLine(probe.Ok ? $"ok in {probe.LatencyMs}ms" : probe.Error);

// Name it on a job…
await client.RunAsync(Operations.Upscale, input,
    new RunOptions { Destination = JobDestination.Saved(bucket.Id) });

// …or opt a single job OUT of the account default. `null` is not the same as
// omitting the field: omitted means "apply my default", OptOut means "don't".
await client.RunAsync(Operations.Upscale, input,
    new RunOptions { Destination = JobDestination.OptOut });
```

`Destinations` also has `ListAsync`, `UpdateAsync`, `DeleteAsync` and
`PresignUploadAsync` — the last mints a 15-minute signed PUT for one object in
your bucket, so a browser can upload an export without ever holding your S3
credentials.

Delivered objects land at `<keyPrefix><yyyy>/<mm>/<dd>/<jobId>.<ext>`, reported
on `result.Delivery.Bucket` / `.Key`. With `DeleteAfterDelivery` the only copy
is yours, and `result.Download` is `null` — that is a documented outcome, not
an error.

---

## What a job cost

Every job response — `CreateJobResult`, `JobView` and `RunResult` all share the
same envelope — reports its own attribution, the three facts `GET /usage`
aggregates:

| | |
| --- | --- |
| `CreditCost` | Credits actually debited. 0 for a free operation, a cache hit, a delivery-only clone, or an unmetered website session. Debited at creation, refunded in full if the job ends `Failed`. |
| `Cached` | The result came out of the result cache: no model ran, nothing was billed. `CreateJobResult.WasCacheHit` reads this flag, falling back to the `200`-not-`202` status. |
| `DeliveryOnly` | The row exists only to deliver an already-cached result to your bucket — cached and free, but still worth polling for `Delivery`. |

```csharp
var job = await client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId(assetId));
Console.WriteLine(job.CreditCost);   // 2
Console.WriteLine(job.Cached);       // false

var again = await client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId(assetId));
Console.WriteLine(again.Cached);     // true — a new JobId, the same result, billed 0
Console.WriteLine(again.CreditCost); // 0
```

---

## Usage and credits

`GetUsageAsync` is `GET /usage`: what the account ran, what it cost and where
it came from, bucketed along **one** dimension at a time. No query means the
last 30 days grouped by day.

```csharp
var report = await client.GetUsageAsync(new UsageQuery
{
    From = DateTimeOffset.UtcNow.AddDays(-7),
    GroupBy = UsageGroupBy.Operation,
    Source = UsageSource.Api,      // optional filters: KeyId, Origin, Operation, Source
});

Console.WriteLine(report.Totals.Jobs);           // 128 — REQUESTS, cache hits included
Console.WriteLine(report.Totals.Credits);        // 214 — what was actually debited
Console.WriteLine(report.Totals.CacheHits);      // 31  — served from cache, billed nothing
Console.WriteLine(report.Totals.ActiveSessions); // 4   — embed sessions seen in the range

foreach (var bucket in report.Series)
{
    Console.WriteLine($"{bucket.Label}: {bucket.Jobs} jobs, {bucket.Credits} credits");
}

Console.WriteLine(report.Bucket("upscale")?.Credits);   // one bucket by key
```

`From`/`To` are `DateTimeOffset`s in any offset — they go on the wire as UTC
instants — and default to the last 30 days. A range wider than 366 days, or a
`From` after `To`, is `ErrorCode.InvalidInput`. `GroupBy` is `Day` (zero-filled
across the range, oldest first), `Key`, `Origin`, `Operation` or `Source`;
everything but `Day` comes back busiest first. `Range` echoes the window the
api actually applied, as ISO-8601 **instants** rather than days.

`report.Keys` is the cap gauge — every live API key on the account with what it
has spent **today**, whatever range you asked for:

```csharp
foreach (var key in report.Keys.Where(k => k.IsCapped && k.RemainingToday < 10))
{
    logger.LogWarning("{Key}: {Used}/{Limit} credits today", key.Name, key.UsedToday, key.DailyCreditLimit);
}
```

Two things to know before writing an assertion against these numbers:

- **A cache hit still creates a job.** One row per *request*, so `Totals.Jobs`
  counts what was asked for and `Totals.CacheHits` says how many of those ran
  no model. `POST /jobs` on a hit therefore answers a **new** `JobId` with the
  **same** `OutputAssetId` as the job whose result it reuses — and the job says
  so itself: `Cached == true`, `CreditCost == 0`.
- **An embed token sees only its own key.** `KeyId` is forced to it and
  `report.Keys` comes back empty — a page-scoped credential must not become an
  account-wide reporting one.

---

## Account, recent jobs and test keys

```csharp
var me = await client.GetAccountAsync();          // secret key only
Console.WriteLine($"{me.AccountId}: {me.Credits} credits on {me.Plan}, key {me.Key.Id} is {me.Key.Mode}");

var recent = await client.ListJobsAsync(new JobListQuery { Limit = 10, Status = JobState.Failed });
foreach (var item in recent)                      // newest first
{
    Console.WriteLine($"{item.CreatedAt} {item.JobId} {item.Job.State} test={item.Job.Test}");
}
```

`ListJobsAsync` defaults to the 20 newest jobs; `Limit` must be 1–100.

An **`sk_test_` key** runs jobs for free: they never touch the result cache,
their result is a placeholder image rather than a model run, and every job
envelope says so with `Test == true` (`false` on a live key, and on a server
that predates the field). A test key is capped at 50 jobs a day.

---

## Webhooks (ASP.NET Core)

Register an endpoint URL on your account and it receives a signed POST every
time a job finishes (and when an embed session ends):

```csharp
var created = await client.WebhookEndpoints.CreateAsync(
    "https://example.com/webhooks/snapnedit",
    new[] { WebhookEndpointEvents.JobSucceeded, WebhookEndpointEvents.JobFailed }); // omit for all events
var secret = created.Secret;   // shown ONCE — store it now

await client.WebhookEndpoints.SetActiveAsync(created.Endpoint.Id, false);    // pause
var rotated = await client.WebhookEndpoints.RotateSecretAsync(created.Endpoint.Id);
await client.WebhookEndpoints.DeleteAsync(created.Endpoint.Id);
```

`WebhookEndpoints.ListAsync()` returns every endpoint — never a secret.
Verify each delivery against the **raw body**, before any JSON parse.

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var secret = builder.Configuration["Snapnedit:WebhookSecret"]!;

app.MapPost("/webhooks/snapnedit", async (HttpRequest request) =>
{
    request.EnableBuffering();
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync();          // the bytes as received
    var signature = request.Headers[SnapneditWebhooks.SignatureHeader].ToString();

    WebhookEvent evt;
    try
    {
        evt = SnapneditWebhooks.ConstructEvent(
            payload,
            signature,
            secret,
            new WebhookVerifyOptions { ToleranceSeconds = 300 });  // replay window
    }
    catch (SnapneditException)
    {
        return Results.BadRequest();                       // bad signature or stale
    }

    switch (evt.Type)
    {
        case WebhookEventType.JobSucceeded:
            Console.WriteLine($"{evt.Data!.JobId} → {evt.Data.OutputAssetId}");
            break;
        case WebhookEventType.JobFailed:
            Console.WriteLine($"{evt.Data!.JobId} failed: {evt.Data.ErrorCode}");
            break;
        case WebhookEventType.SessionEnded:
            Console.WriteLine($"session {evt.Session!.SessionId}: {evt.Session.Jobs} jobs, {evt.Session.Credits} credits");
            break;
        default:
            break;                                          // forward-compatible no-op
    }

    return Results.Ok();
});

app.Run();
```

`SnapneditWebhooks.Verify(payload, signature, secret)` returns a plain `bool` if
you would rather branch than catch. It never throws for untrusted input:
a malformed header, a bad MAC and a stale timestamp are all `false`.

The header is `X-Snapnedit-Signature: t=<unix seconds>,v1=<hex>`; the signed
string is `{t}.{rawBody}`, MAC'd with HMAC-SHA256 under your endpoint secret and
compared in constant time. `v1` may appear more than once during a secret
rotation — any match accepts the delivery.

---

## Embedding the editor

```csharp
// On your server, with your sk_ key. Hand the TOKEN to the browser, never the key.
var token = await client.Embed.CreateTokenAsync(new EmbedTokenOptions
{
    TtlSeconds = 3600,
    EndUserId = user.Id,
    MaxCredits = 20,
    AllowedOperations = new[] { Operations.RemoveBackground, Operations.Upscale },
});
```

`Embed.CreateSessionAsync(publishableKey, hostOrigin)` is the other door: the
frame's own exchange of a `pk_` key for the same short-lived token.

---

## Errors

Every failure is a `SnapneditException` carrying a **machine-readable**
`ErrorCode` and the HTTP `Status`. Branch on the code; the message is human text
and changes.

```csharp
try
{
    await client.RunAsync(Operations.Upscale, JobInput.FromFile("cat.jpg"));
}
catch (SnapneditException e) when (e.Code == ErrorCode.PaymentRequired)
{
    // out of credits (402)
}
catch (SnapneditTimeoutException)
{
    // polling ran out of time — the job may still be running
}
catch (SnapneditException e)
{
    logger.LogError("snapnedit {Code} ({Status}): {Message}", e.Code, e.Status, e.Message);
}
```

| `ErrorCode` | Wire | Typically |
| --- | --- | --- |
| `InvalidInput` | `invalid_input` | 400 — bad params, an unknown param key, a missing `maskAssetId` |
| `UnsupportedMime` | `unsupported_mime` | 400 — not `image/png`, `image/jpeg` or `image/webp` |
| `TooLarge` | `too_large` | 400/413 — over the upload ceiling |
| `NotFound` | `not_found` | 404 — no such job/asset/destination, **and** anything belonging to another account |
| `InputFetchFailed` | `input_fetch_failed` | terminal job failure fetching `inputUrl`; refunded |
| `ProviderFailed` | `provider_failed` | a model provider failed; retryable |
| `ProviderExhausted` | `provider_exhausted` | every provider for the operation failed |
| `RateLimited` | `rate_limited` | 429 |
| `BotCheckFailed` | `bot_check_failed` | a bot-protection challenge was not satisfied |
| `Unauthorized` | `unauthorized` | 401 — missing or unknown credential |
| `Forbidden` | `forbidden` | 403 — e.g. an anonymous caller using `inputUrl` |
| `PaymentRequired` | `payment_required` | 402 — out of credits |
| `Internal` | `internal` | 5xx, and the fallback for any code this package does not know |

A job that fails surfaces the job's own `errorCode` as `e.Code` with
`Status == 200` — the HTTP call succeeded, the work did not.

---

## Operations

`Operations` has a constant for each id, and a typed params record for each
operation. A cost of 0 is free; a **cache hit is never billed** (it still
records a job — a new `JobId` with the same `OutputAssetId`, reported as
`Cached` with `CreditCost == 0`) and a terminal failure is **refunded**.

| Operation | Constant | Credits | Mask | Params record |
| --- | --- | --- | --- | --- |
| `remove-background` | `Operations.RemoveBackground` | 1 | | `RemoveBackgroundParams` |
| `upscale` | `Operations.Upscale` | 2 | | `UpscaleParams` (`Factor`) |
| `unblur` | `Operations.Unblur` | 1 | | `UnblurParams` |
| `colorize` | `Operations.Colorize` | 1 | | `ColorizeParams` |
| `style-transfer` | `Operations.StyleTransfer` | 2 | | `StyleTransferParams` (`Style`) |
| `retouch` | `Operations.Retouch` | 1 | | `RetouchParams` |
| `beautify` | `Operations.Beautify` | 1 | | `BeautifyParams` (`Amount`) |
| `magic-eraser` | `Operations.MagicEraser` | 2 | ✅ | `MagicEraserParams` |
| `generative-fill` | `Operations.GenerativeFill` | 3 | ✅ | `GenerativeFillParams` (`Prompt`, `Mode`) |
| `remove-watermark` | `Operations.RemoveWatermark` | 2 | ✅ | `RemoveWatermarkParams` |
| `ai-denoise` | `Operations.AiDenoise` | 1 | | `AiDenoiseParams` (`Strength`) |
| `replace-sky` | `Operations.ReplaceSky` | 2 | | `ReplaceSkyParams` (`Sky`) |
| `relight` | `Operations.Relight` | 2 | | `RelightParams` (`Direction`) |
| `replace-background` | `Operations.ReplaceBackground` | 2 | | `ReplaceBackgroundParams` (`Background`) |
| `strip-metadata` | `Operations.StripMetadata` | 1 | | `StripMetadataParams` |
| `auto-remove-watermark` | `Operations.AutoRemoveWatermark` | 2 | | `AutoRemoveWatermarkParams` (`Strength`) |
| `resize-image` | `Operations.ResizeImage` | **0** | | `ResizeImageParams` (`Width`, `Height`, `Fit`, `Format`, `Quality`) |

Mask-guided operations refuse the job without a mask. Pass the bytes and let the
client do the upload:

```csharp
await client.RunAsync(Operations.MagicEraser, JobInput.FromFile("photo.png"), new RunOptions
{
    Mask = JobInput.FromFile("mask.png"),   // uploaded, and params.maskAssetId set for you
});
```

Deployments may **defer** an operation whose model has not shipped yet
(`generative-fill`, `relight`, `auto-remove-watermark` at the time of writing).
`ListOperationsAsync()` is the live catalog — accepted mime types, maximum input
dimension, params JSON Schema and `RequiresMask` — and the thing to trust at
runtime.

---

## Public API at a glance

| | |
| --- | --- |
| `new SnapneditClient(apiKey, options?)` | The client. Thread-safe, long-lived, `IDisposable`. |
| `RunAsync(operation, input, options?, ct)` | Upload → create → poll → download. |
| `CreateJobAsync` / `GetJobAsync` / `WaitForJobAsync` | The steps, separately. |
| `UploadAsync(bytes\|JobInput, mime?, ct)` | `POST /uploads` → PUT → confirm. |
| `ListOperationsAsync(ct)` | The live operation catalog. |
| `GetUsageAsync(query?, ct)` | Jobs, credits and embed sessions over a range, bucketed. |
| `GetAccountAsync(ct)` | The account, its balance and the key's live/test mode. |
| `ListJobsAsync(query?, ct)` | The newest jobs, each with its full envelope. |
| `WebhookEndpoints.{List,Create,SetActive,Delete,RotateSecret}Async` | Webhook endpoint management. |
| `Destinations.{List,Create,Update,Delete,Test,PresignUpload}Async` | Saved storage destinations. |
| `Embed.{CreateSession,CreateToken}Async` | Embedded-editor tokens. |
| `Designs.{Create,CreatePages,Render}Async` | Server-side design composition and rendering. |
| `SnapneditWebhooks.{Verify,ConstructEvent,Parse,ComputeSignature}` | Webhook signatures. |
| `JobInput.{FromBytes,FromStream,FromFile,FromUrl,FromAssetId}` | Where an image comes from. |
| `JobDestination.{PresignedPut,Saved,OptOut}` | Where the result goes. |

---

## Building and testing

```sh
dotnet build -warnaserror
dotnet test                                # unit + conformance
dotnet test --filter Category!=Conformance # unit only
dotnet pack -c Release
```

The **conformance suite** is one test per scenario in the monorepo's
`test/conformance/scenarios.json` — the language-neutral contract every
snapnedit SDK is held to, all 29 of them. Each test is named with its scenario
id, so a changed scenario is a `grep` away from its test. The fixture boots
`node scripts/conformance-server.mjs` from the repo root (the real Fastify API
and the real worker, in one process, on an ephemeral port) and terminates it
with `SIGTERM` on teardown. It needs `npm ci && npm run build` in the monorepo
first, and is skipped by filtering on `Category!=Conformance` in a standalone
checkout.

---

## About this repository

Mirrored from the [snapnedit monorepo](https://github.com/Snap-N-Edit), where it
is developed alongside the API it speaks to. Issues and pull requests are
welcome here; the canonical source lives in the monorepo and lands here by
subtree split, history intact.

Full API reference: **<https://snapnedit.com/docs/api-reference>**.

MIT licensed — see [LICENSE](./LICENSE).
