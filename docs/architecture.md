# Architecture

Uptime King runs as an ASP.NET Core API with hosted workers, an Angular frontend, and PostgreSQL. The API and workers share the domain model and database. Angular's development proxy and production Nginx proxy keep HTTP requests and SignalR on the same browser origin.

## A check, from claim to dashboard

1. On startup and every two seconds, `MonitorWorker` asks `MonitorClaimStore` for due, unpaused, non-demo monitors up to its remaining capacity. A PostgreSQL transaction uses `FOR UPDATE SKIP LOCKED`, sets a two-minute lease and a new token, then commits.
2. Up to eight probes run concurrently in separate dependency injection scopes. Completed slots are refilled while slower checks remain in flight. Shutdown cancels and awaits those checks; network calls do not hold the claiming transaction open.
3. The named `probe` client issues a GET with `ResponseHeadersRead`. Status codes 200–399 are successful. Redirects are not followed, and response bodies are not downloaded. Polly gives each attempt ten seconds, allows one transient retry, and bounds the whole operation to 25 seconds.
4. `MonitorRunner` locks the monitor row again. It discards the result if the monitor was deleted, paused, edited or claimed under a different lease token.
5. The check, monitor state, incident transition and notification outbox records commit together. A partial unique index permits only one unresolved incident per monitor.
6. SignalR emits a best-effort `dashboardChanged` hint after commit. A broadcast failure cannot turn a saved operation into an HTTP error. The browser combines hints arriving within 250 ms into one refresh, polls every 30 seconds, and retries failed connections. Version counters prevent older dashboard responses or expired-session responses from replacing a newer login.

The next due time is the check's start time plus its configured interval. Actual timing depends on workload, request duration and scheduler availability. Expired leases can be reclaimed after a process failure.

## Health and incidents

| Observation | Monitor state | Incident action |
| --- | --- | --- |
| No result yet | Pending | None |
| Successful response below the slow threshold | Operational | Resolve any open incident |
| Successful response at or above the slow threshold | Degraded | Resolve any open incident |
| First consecutive failure | Degraded | None |
| Second consecutive failure | Down | Open an incident and queue alerts |
| Further failures | Down | Keep the same incident; no repeated opening alerts |
| Paused | Paused in the dashboard | Stop scheduling; retain any open incident |

A failed attempt that succeeds on Polly's retry counts as one successful check. Response time includes retries and their delay. Incidents start at the second failed check, when failure is confirmed. Acknowledging an incident does not resolve it or stop checks.

Editing or pausing invalidates the active lease. Deleting a monitor cascades through its checks, incidents and queued notifications.

## Measurements

- **Uptime:** successful checks divided by total recorded checks during the last 30 days. The workspace figure weights monitors by check count. It is not time-weighted availability.
- **Response average:** average elapsed time across recorded checks, including failures and retry time. The summary uses 30 days; the response chart uses 24 hours. Both weight by check count. It is not browser page-load time.
- **History bars:** UTC hour buckets for the last 24 hours. A bucket is healthy when all its checks succeeded, down when none did, and mixed otherwise. The window spans 25 calendar-hour buckets, including the partial hours at each end. Empty hours stay grey; tooltips expose the time and average response. The chart uses elapsed time on its x-axis and leaves missing hours disconnected.
- **No data:** shown as an em dash or empty state. Missing checks are not invented and gaps do not count as downtime.
- **Retention:** hourly cleanup removes raw non-demo checks and delivered notifications older than 30 days. Incident history is retained; the dashboard returns every open incident plus the latest 100 resolved incidents; detail views return the latest 100 checks.

The public demo is seeded once, with a fixed observation time. Its historical chart stays coherent across restarts. Guest endpoints read only demo monitors; authenticated endpoints read only real monitors. Demo rows are never scheduled.

## Notifications

An incident transition creates one outbox row per enabled channel in the same database transaction. Every five seconds, the dispatcher processes up to five due rows. Each row is locked with `SKIP LOCKED` and committed in its own transaction, so cancellation during a later send cannot roll back an earlier successful delivery. SMTP or Discord delivery gets a 20-second cancellation budget. Failed attempts back off exponentially; six attempts exhaust a delivery. Delivery history exposes attempts, success and a sanitised error.

Delivery is **at least once**, not exactly once. If the process sends a message and dies before committing the delivery result, the next attempt can send it again. The dispatcher holds row locks during sending; that is a deliberate small-workspace tradeoff, not a high-throughput queue design.

Delivery uses the current channel settings, so changing an address also changes where pending messages go. Disabling a channel prevents queued messages from being sent through it; they stay queued without consuming attempts until the channel is enabled again. Recovery delivery waits for the corresponding opening alert to succeed or exhaust its retries. Acknowledging an incident does not cancel its already queued opening alert.

Discord destinations are limited to HTTPS `discord.com/api/webhooks/{id}/{token}` URLs. Tokens are encrypted through ASP.NET Core Data Protection. Discord mentions are disabled in the payload. Requests wait for server confirmation, and long messages are truncated to fit the [webhook content limit](https://docs.discord.com/developers/resources/webhook#execute-webhook). SMTP supports STARTTLS and optional authentication. The local configuration uses Mailpit without TLS.

## Authentication and outbound requests

The single workspace password is supplied by configuration, checked with a fixed-time comparison, and never stored in the application database. Sign-in is rate-limited. Session cookies are HTTP-only and SameSite Strict, with an eight-hour lifetime and sliding renewal. Production cookies require HTTPS. Each authenticated request validates a password-version stamp inside the encrypted ticket, so changing or removing the configured password rejects existing sessions on their next request. An already-connected hub only emits refresh hints; private data is still fetched through the authenticated API. Unsafe API methods require the `X-UptimeKing: 1` header; there is no cross-origin API policy. Password-version validation uses ASP.NET Core’s [ValidatePrincipal event](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0#react-to-back-end-changes).

Only public HTTP/HTTPS endpoints on standard ports are accepted. Hostnames are resolved at connection time, all answers must be public, and the socket connects to an already validated address. Probes disable redirects, cookies and proxies. This keeps client-supplied URLs from reaching local services or cloud metadata addresses. Production egress rules provide an additional deployment boundary.

ASP.NET Core request tracing and custom check spans/metrics feed Aspire through OTLP. HTTP client URL logging is disabled to avoid logging configured URL secrets. Check errors persist sanitised descriptions rather than raw exception messages.

## Scaling boundaries

Database claims and lease fencing allow competing schedulers to avoid duplicate commits. The shipped deployment still assumes **one API instance**: SignalR uses in-memory connections, migrations run at startup, and Data Protection keys live in a mounted volume.

Scaling the API needs a SignalR backplane or service, shared protected keys, a migration job, and a trusted-proxy/rate-limiting plan. Higher monitor counts would also benefit from pre-aggregated history and separate worker capacity. The current 100-monitor limit is an application boundary, not a published performance claim.
