# RaidTools: accept gzip-compressed Extractor uploads

A prompt for a RaidTools session, written from the uploader side. **This is a summary, never the
contract.** The transport is described in this repo's [export-schema.md](export-schema.md) ("Transport"),
and the uploader's `RslCompanionApiClient.UploadConsolidatedAsync` is what actually sends it.

## Why

`POST /api/sync/consolidated/raw` carries a whole account: about **6.2 MB of JSON** on a large account
(5,800 artifacts, 870 champions). JSON like this compresses very well. Measured on a live 11.80.0 export
on 2026-10-09:

| Body | Size |
|---|---|
| plain JSON | 6,156 KB |
| gzip (optimal) | 363 KB (17×), 36 ms to compress |
| brotli (optimal) | 383 KB |

Cloud Run doesn't bill for incoming traffic, so this saves no money. It saves the **player's upload time**:
6 MB on a slow home uplink (1–5 Mbit/s up is common) is 10–50 s, the slowest step of an update, and
0.4 MB is 1–3 s. gzip and not brotli: it is slightly smaller here, and ASP.NET Core decompresses it out of
the box.

**Uploader 1.48 already sends gzip** (`Content-Encoding: gzip`). It is safe against a server that can't
read it yet. Prod today answers the binary body with ASP.NET's automatic model-binding 400
(`application/problem+json`; the action binds `[FromBody] JsonElement` under `[ApiController]`). The
uploader treats that answer, or a 415, as "this server can't read gzip". It resends the same body plain
and sends plain to that server for the rest of the session. Until this lands, each session's first
upload therefore costs one extra round trip. The controller's own 400s are plain `{message}` JSON, so
they are never mistaken for it and never resent.

## The change

### 1. Turn on request decompression (`Program.cs`)

```csharp
builder.Services.AddRequestDecompression();   // gzip, deflate and br are registered by default
...
app.UseRequestDecompression();
```

**Placement matters:** after the oversized-body middleware (the `try { await next(); } catch
(BadHttpRequestException 413)` block), so a decompressed body over the limit still gets the friendly
413 JSON. And before `MapControllers`. Putting it right before `app.UseCors("Frontend")` is fine.

### 2. Nothing in the controller changes

The middleware swaps the request body for a decompressing stream and removes the `Content-Encoding`
header, so `SyncConsolidatedRaw` binds the same `JsonElement` from the same JSON.

## Things to get right

- **The size limit applies to the DECOMPRESSED body**, which is what makes this safe against a zip
  bomb. `[RequestSizeLimit(ImportLimits.MaxImportBytes)]` (24 MB) keeps meaning what it meant. The
  middleware enforces the endpoint's limit on the decompressed stream and throws the same 413
  `BadHttpRequestException`, which the existing handler turns into the JSON message. Test it with a
  small gzip body that inflates past 24 MB, and expect 413 rather than an out-of-memory or a 500.
- **Log the wire size beside the payload size** in the import's log line, so savings and abuse are both
  visible. `payloadBytes` already logs the decompressed size. `Request.ContentLength` read *before* the
  middleware (or `Content-Length` from a small logging middleware) is the compressed one.
- **An unsupported `Content-Encoding`** (anything but gzip/deflate/br) passes through the middleware
  untouched and fails binding as today, a 400. Nothing to add.
- **Other endpoints** can receive compressed bodies too once this is on. That is harmless: every
  `[FromBody]` reads the decompressed stream, and every size limit applies after decompression.
- **No frontend change.** Browsers don't compress request bodies on their own. The Angular site's
  uploads are unaffected.

## Tests

- An integration test (`WebApplicationFactory`) posting the same consolidated fixture twice, once plain
  and once gzip with `Content-Encoding: gzip`, gets the same 200 and the same stored snapshot.
- A gzip body that inflates past `MaxImportBytes` gets the 413 JSON, not a 500.

## Rollout

Ship to `dev`, check an upload from a 1.48 uploader signed in to the dev server (the log line's wire
size should drop to ~6%), then **Release to prod**. No uploader change is needed afterwards: the
fallback only triggers on a server that can't read gzip, so once prod can, every upload is compressed.
