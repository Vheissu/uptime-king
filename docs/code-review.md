# Code review · 12 October 2026

The review covered scheduled checks, database concurrency, incident and notification delivery, authentication, outbound requests, Angular state, browser rendering and local deployment. The fixes below have regression coverage unless marked as a browser or configuration check.

| Issue | Change and evidence |
| --- | --- |
| One slow probe delayed the next batch even when other slots were free | The worker now claims only its available capacity on each tick. An integration test holds one probe open while all nine distinct monitors begin checking. |
| A monitor without a claim token could pass the completion guard | The runner rejects unclaimed work. PostgreSQL tests cover disjoint scheduler claims, expired lease replacement and duplicate completion. |
| A failed SignalR broadcast could return an error after a database write committed | Refresh hints are bounded and best effort. A failing hub test verifies that publishing does not throw; the existing live integration test still checks delivery. |
| The latest-100 query could hide an old unresolved incident | All open incidents are included alongside the latest 100 resolved incidents. The regression seeds 101 recoveries and an older open outage. |
| Notification batch rollback could resend alerts that had already succeeded | Each delivery commits separately. A cancellation test interrupts the second send and verifies the first remains delivered. |
| Recovery alerts could overtake failed opening alerts | Recovery waits while its opening alert remains retryable. Disabling a channel preserves queued attempts. Both cases have database tests. |
| A missing pause flag could be interpreted as `false` | The flag is required in JSON, and malformed request bodies preserve their HTTP 400 status through the exception handler. A regression verifies that an empty request cannot resume a paused monitor. |
| Concurrent first settings saves could violate the singleton primary key | A transaction-scoped advisory lock serializes saves. Four concurrent API requests must all succeed and leave one row. |
| Changing the workspace password left existing cookies valid | The encrypted ticket carries a password-version stamp, checked on every authenticated request. Tests rotate and remove the password. |
| Failed session bootstrap and failed SignalR startup required a page reload | Session bootstrap and live connections retry. Tests also cover old 401 responses arriving after a new login, clearing data on logout, and combining live refresh hints. |
| The response chart distorted time gaps and displayed a 30-day average over a 24-hour plot | The chart uses timestamps, leaves missing hours disconnected, and displays a separately calculated 24-hour average. Database and frontend tests cover these distinctions. |
| Empty hours vanished from monitor history; demo aggregates looked like individual checks | Empty hours remain visible, and the demo detail view explicitly shows hourly aggregates. The backend tests assert missing-hour output; browser checks cover presentation. |
| The outbound policy allowed Azure’s platform address | Blocked `168.63.129.16`, including mapped IPv6 forms, and added regressions. Microsoft documents this address as [WireServer](https://learn.microsoft.com/en-us/azure/virtual-network/what-is-ip-address-168-63-129-16). |
| Public IPv6 literal URLs were rejected despite public IPv6 addresses being allowed | Literal addresses are validated before applying hostname rules. Tests cover public and non-public IPv6. |
| Production asset requests could receive SPA HTML, and entry HTML could remain cached across releases | Nginx returns 404 for missing bundles and marks entry HTML for revalidation. The container smoke test checks these headers and status codes. |

The notification form and response chart now have separate Angular components. Probe-client configuration, database claiming and check completion also have separate files. This keeps those behaviours testable without adding a generic repository or message-bus abstraction.

The check-history scroll region is keyboard-focusable; the browser accessibility scan covers the expanded dialog.

The UI copy audit found no remaining “watchful eye”, “all accounted for”, or similar slogans in tracked source. Measurement labels were tightened where their time windows were ambiguous.

## Remaining boundaries

This is still a single-workspace, single-API application. The scheduler has eight concurrent slots, so consistently slow targets can delay checks; the configured interval is not a delivery guarantee. Uptime measures successful recorded checks, not an SLA or multi-region availability.

Notifications remain at least once: a process failure after an external send but before its database commit can duplicate that message. Sending holds one delivery row lock for up to 20 seconds. These limits are documented in [architecture](architecture.md), alongside the work required for multiple API instances.

No real SMTP provider or Discord channel was contacted during the review. Mailpit verifies SMTP locally; Discord tests inspect requests and failure handling. Deployment beyond localhost still requires the steps in [hosting](operations.md#hosting).
