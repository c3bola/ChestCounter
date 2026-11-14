# API synchronization

The desktop app writes its API settings to `api-config.json` in the same data folder as `data.json` (normally `%APPDATA%\ChestCounter`). It holds only distributable settings:

```json
{
  "enabled": true,
  "baseUrl": "https://api.fortcards.me/api/v1/"
}
```

The file never contains a token. A `bearerToken` entry left by an earlier version is removed the next time the file is loaded.

## Authentication

`ApiAuthService` owns the JWT. It is kept in memory only (never written to `api-config.json`, the queue, the log or the UI), and the Settings window shows just its state: not started, obtaining authorization, authenticated, token expired, or authentication failed.

- Before each protected request the service reuses the in-memory token if its `exp` claim is still in the future (30 s skew); otherwise it requests a new one. Concurrent callers share one token request.
- A token must have three segments and an `exp` claim, and must not already be expired; the signature is left to the API.
- On HTTP 401 the rejected token is discarded, a new one is requested and the request is repeated **once**. This is safe because every protected call here is a `GET` or carries an idempotency key. A second 401 is reported as a normal failure, so there is no authentication loop.
- A failed token request pauses automatic synchronization for 60 s; manual actions can retry immediately.
- Manual and automatic synchronization and the connection test all go through the same service.

### `POST /auth/mock`

The app requests the token with `POST {BaseUrl}auth/mock` (path in `ApiAuthService.MockEndpoint`), without an `Authorization` header, and expects `{"success": true, "token": "<jwt>", "user": {...}}`. `success: false`, a missing token, invalid JSON, HTTP errors, connection failures and timeouts are reported as authentication failures.

The route is absent from `reference/openapi.yaml` (v1.2.0), but the project owner confirmed it against the production API. The contract should still be updated to document it and the JWT lifetime (`exp`). Setting `ApiAuthService.MockEndpointAuthorizedForProduction` to `false` disables automatic authorization again.

For successful `POST /auth/mock` responses, `ApiSyncService` buffers the response content before reading its diagnostic preview. `ApiAuthService` then reads the same cached content to extract the token; this avoids consuming a one-shot HTTP response stream twice.

## Endpoints

Public (`security: []`, no token sent): `POST /auth/telegram`, `POST /auth/{provider}`, and the token request itself, which is sent without an `Authorization` header. The app does not call the first two.

Protected (global `bearerAuth`; the app sends the Bearer token): `GET /replay-files` (connection test), `POST /replay-files`, `GET /replay-files/{sha256}`, `POST /replay-files/{sha256}/processing-attempts`.

## Queue and retries

Replay analysis and the JSON history remain local-first. After `data.json` is saved, the replay's original SHA-256, an idempotent ingestion request, and its retry state are persisted in `api-sync-queue.json`. Pending operations resume in the background on the next launch. Transient network errors, HTTP 408, 429, 5xx, and in-progress HTTP 202 responses are retried with backoff; permanent rejections remain available for a manual retry.

The integration currently sends only fields available in the local models: match identity and metadata, the tracked participant and known teammates, observed containers/interactions, and rewards with an original prize ID. The local analyzer does not produce match-wide statistics, vehicles, POI visits, or safe-zone phases, so these are omitted. The connection test uses the documented authenticated `GET /replay-files` endpoint because the OpenAPI contract has no health-check endpoint.

The Settings actions open an operation monitor that reports configuration and queue validation, authorization, current replay, progress, and each actual HTTP request/response. Selecting a log entry shows its method, endpoint, sanitized request body, HTTP status, response summary, duration, and retryability. Closing the monitor does not cancel the operation. Diagnostic metadata is appended to `api-sync.log`, rotated at 1 MiB with three retained archives; request and response bodies and authentication headers are not written to this file, and token-like values are redacted from the monitor.

## Local replay diagnostic laboratory

The main window's **Testar arquivo local** button opens a separate replay laboratory. It does not process a file on open or persist analysis results. Its read and transcode stages use the existing replay header probe and `ReplayAnalyzer`; pre-send uses `ReplayIngestionMapper`.

The shared replay reader has a pre-existing Debug-build behavior that can write parser `.dump` files to the process working directory when it encounters malformed properties; the laboratory uses the same reader and does not change that behavior.

The manual send uses `ApiSyncService` with the configured base URL and authentication, but stores the selected replay in a unique temporary queue rather than the normal `api-sync-queue.json`. It requires an explicit confirmation, shows the HTTP response and selected response headers, compares the captured request bodies with the prepared JSON, and removes the temporary queue and logs after the operation. Response bodies use the synchronizer's bounded 8 KiB preview, with a truncation marker when necessary. It never writes the JWT to the UI or diagnostic logs.

To remove the laboratory, delete `src/ChestCounter/Views/ReplayDiagnostic/`, remove the **Testar arquivo local** button from `Views/MainWindow.axaml`, and remove its window-opening handler and field from `Views/MainWindow.axaml.cs`. Keep the auth-response buffering fix and its regression test; those are independent of the laboratory and also protect normal synchronization.

Changing the API URL in Settings requires the local password `fnbr26`; this is only an accidental-change guard, not a security boundary.
