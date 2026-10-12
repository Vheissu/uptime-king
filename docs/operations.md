# Running and operating Uptime King

## Local Docker setup

Run `./scripts/setup.sh`, then `docker compose up --build -d`. The generated `.env` contains unique credentials and is created with permissions `0600`. Compose binds published ports to loopback.

The web port defaults to 8099. Mailpit uses 8029 for its inbox and 1029 for SMTP. Override `WEB_PORT`, `MAIL_UI_PORT` or `SMTP_PORT` in `.env` if needed. PostgreSQL is published on 54329; the API is on 5080.

```sh
docker compose ps
docker compose logs -f api
curl --fail http://localhost:8099/health/ready
```

`/health/live` confirms the process responds. `/health/ready` also checks the database connection. Migrations run before the API starts accepting requests.

Mailpit captures local email. Enable the email channel under Notifications with any syntactically valid address, then inspect the inbox. There is no external mail delivery in the default stack. Discord requires a real webhook and sends to the associated channel when enabled.

## Aspire development

The AppHost needs .NET 10, Node 24, Docker and the Aspire CLI on `PATH`. `./scripts/dev.sh` reads `.env`, supplies the workspace password as an Aspire parameter, and starts the resource graph. PostgreSQL and Mailpit run in containers; the API and Angular dev server run as host processes.

Compose and Aspire should not run at the same time with their default ports. Their PostgreSQL volumes are separate. Check the Aspire dashboard for assigned database and mail ports. Angular is at port 4200. The dashboard address and access token are printed by Aspire.

## Manual development

Start the local infrastructure and export configuration in the same shell as the API:

```sh
./scripts/setup.sh
docker compose up -d postgres mail
set -a
source .env
set +a
export ConnectionStrings__uptime="Host=localhost;Port=54329;Database=uptime;Username=uptime;Password=$POSTGRES_PASSWORD"
export Auth__Password="$UPTIME_PASSWORD"
export Smtp__Host=localhost
export Smtp__Port=1029
export Smtp__UseTls=false
dotnet run --project src/UptimeKing.Api
```

In another terminal:

```sh
cd src/UptimeKing.Web
npm ci
npm start
```

The development proxy defaults to `http://localhost:5080`. Set `API_URL` when starting Angular to use a different API address. API clients must send `X-UptimeKing: 1` on POST, PUT and DELETE requests.

## Configuration

Use environment variables with double underscores for nested .NET configuration keys.

| Variable | Purpose / default |
| --- | --- |
| `ConnectionStrings__uptime` | Required PostgreSQL connection string |
| `Auth__Password` | Workspace password, at least 16 characters. Missing means sign-in is unavailable. |
| `DataProtection__Path` | Key directory; defaults to `keys` under the working directory |
| `Demo__Enabled` | Seed fictional sample data at startup; defaults to `true` |
| `Monitoring__Enabled` | Run monitoring, notification and retention workers; defaults to `true` |
| `Smtp__Host` | SMTP hostname; defaults to `localhost` |
| `Smtp__Port` | SMTP port; defaults to `1025` |
| `Smtp__UseTls` | Require STARTTLS; defaults to `true` (local stacks override to `false`) |
| `Smtp__From` | Sender address; defaults to `uptime@localhost` |
| `Smtp__Username`, `Smtp__Password` | Optional SMTP authentication |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional telemetry collector; supplied by Aspire |

`UPTIME_PASSWORD` and `POSTGRES_PASSWORD` are setup-script/Compose variables mapped to application configuration. Recipient addresses and encrypted Discord webhook settings are managed through the application.

Setting `Demo__Enabled=false` stops seeding; it does not remove previously seeded data. Demo data is fictional and contains no workspace URLs.

## Hosting

The included Compose file is a local development configuration, including `ASPNETCORE_ENVIRONMENT=Development` and Mailpit. To host it:

1. Serve Nginx behind HTTPS on a domain you control. Preserve WebSocket upgrades for `/hubs/` and keep API traffic on the same browser origin.
2. Run the API with `ASPNETCORE_ENVIRONMENT=Production`, a strong workspace password, and a managed secret source. Secure cookies will then be required.
3. Keep PostgreSQL and the API private. Apply outbound network rules appropriate for public website monitoring. Restrict access to the operator workspace.
4. Persist and protect the Data Protection key directory alongside database backups. The default keys have filesystem protection; use an appropriate key-encryption provider for your hosting environment. Losing keys invalidates sessions and makes stored Discord tokens unreadable.
5. Configure a real SMTP sender with STARTTLS on the provider's submission port, or enable Discord. Mailpit is only a local capture service.
6. Review trusted-proxy handling and sign-in rate limits. The default limiter sees the immediate proxy address, so proxied users share its quota.

This version has one shared operator account. It has no account recovery, tenant separation or role system. Changing or removing the configured password rejects existing cookies on the next API request. Environment-variable changes require an API restart. Do not use the shared workspace to give unrelated clients access to each other's monitors.

## Data and recovery

Back up PostgreSQL and the Data Protection keys. Check history is removed after 30 days, so retain external backups if longer raw history matters. A monitor deletion permanently removes its checks, incidents and outbox rows.

After changing entities, create an EF migration and review the SQL before deployment:

```sh
dotnet tool restore
dotnet ef migrations add YourChange --project src/UptimeKing.Api
dotnet ef migrations script --idempotent --project src/UptimeKing.Api
```

Use one migrator when deploying multiple instances. The default startup migration path is intended for the single-instance setup.

## Common issues

- **Docker cannot bind a port:** stop another Uptime King launch or change the relevant local mapping. Avoid running Compose and Aspire together on port 5080.
- **Sign-in is unavailable:** supply `Auth__Password` with at least 16 characters. In Compose, rerun the setup script if `.env` is missing.
- **A target cannot be checked:** private/internal hosts, non-standard ports and hostnames resolving to reserved addresses are intentionally rejected. A redirect confirms reachability of the configured URL; it does not check the redirect destination.
- **A delivery has six failed attempts:** correct the channel configuration. There is no manual resend action in this version; later incident transitions create new deliveries.
- **A dashboard connection is offline:** the client still refreshes every 30 seconds. Check WebSocket proxy support, the next polling interval retries a failed connection. An expired session returns to the demo so you can sign in again.
