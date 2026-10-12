# Verification notes

Verified on 12 October 2026 with .NET SDK 10.0.401, Node 24.21.0 and Docker on macOS. These notes cover the initial build and the subsequent code review; the GitHub Actions run is the ongoing source for CI status.

## Automated checks

- **88 backend tests passed**, with no failures or skips. Integration tests use real PostgreSQL 17 and Mailpit containers.
- **18 frontend tests passed**, including refresh ordering, session expiry, network recovery, CSRF headers and error responses.
- Csharpier and Prettier formatting checks passed.
- NuGet vulnerability checks included transitive backend packages. `npm audit --omit=dev` checked production frontend dependencies. Both reported no known vulnerabilities at review time.
- Angular production compilation passed with strict TypeScript and strict template checking, within the configured initial bundle budget.
- Both production Docker images built, and the application served through Nginx with its content security policy enabled.

Backend coverage includes monitor state transitions, destination validation, HTTP probing, authenticated API changes, sample-data isolation, stale lease rejection, competing completion of the same lease, transactional incidents and outbox records, encryption of notification secrets, real local SMTP delivery, retry backoff, cascading deletion, and authenticated SignalR delivery. Discord payload tests use a recording HTTP handler, with no external channel messages.

## Browser checks

The production Docker application was exercised in Chromium:

- Sign in, start from an empty workspace, and create a monitor for `https://example.com`.
- Observe real HTTP 200 checks, response time, calculated uptime and the live connection indicator.
- Open individual check history, pause, edit and delete the monitor.
- Enable and save local email settings, then restore them to disabled and sign out.
- Load the production dashboard with no browser console errors.
- Recheck all five guest views, the mobile overview, authenticated notification settings and the hourly demo-history dialog after the review changes.

Axe WCAG 2 A/AA and 2.1 AA scans passed on the five guest views: Overview, Monitors, Incidents, Clients and Notifications. The narrow overview, open mobile navigation and mobile sign-in dialog also passed. The 390-pixel overview had no horizontal page overflow. These automated scans are useful regression checks, not a claim of a full accessibility audit.

## Aspire

Ran `./scripts/dev.sh` with the documented prerequisites. Aspire reported the API, web application, PostgreSQL database and Mailpit running and healthy. The browser loaded Angular at port 4200, and its proxy returned the six-monitor demo from the API. The stack stopped cleanly through the Aspire CLI.

## Screenshots

Images in `docs/screenshots/` are captured from the running production frontend. The overview and incident images show the isolated demo workspace; the mobile image uses a 390-pixel viewport. They contain fictional client data, not customer records.

## External delivery boundary

Email was verified against Mailpit. Discord request formatting, encrypted destination handling, message limits and disabled-channel behaviour were tested locally. Delivery to a real SMTP provider or Discord channel depends on credentials supplied by the operator and was not exercised for this repository.
