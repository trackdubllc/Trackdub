# trackdub-api

**api.trackdub.com** — Cloudflare Worker backend for the Trackdub portal.
Better Auth + D1 + Drizzle + Hono. This repo owns authentication, authorization,
and API routing. The portal (`portal.trackdub.com`) is a browser-only SPA that
talks to this Worker with `credentials: "include"`; no backend logic lives in
the portal.

> **Scope:** Auth, health, version, languages, admin invite, and the **dubs job
> scaffold** (`/api/dubs*`: upload to R2, create/list/get/cancel Queued jobs).
> Billing, keys, webhooks, and the real cloud dub pipeline worker are still
> later work. Jobs do **not** auto-complete.

## Stack

- **Cloudflare Workers** (`src/index.ts`, Hono app)
- **Better Auth** — email/password, invite-only, host-only session cookies
- **Cloudflare D1** (SQLite) via **Drizzle ORM** + Drizzle Kit migrations
- **Cloudflare R2** (`MEDIA` binding) — media uploads for `/api/dubs/upload`
- **Zod** for config + request validation
- **Vitest** (`@cloudflare/vitest-pool-workers`) — tests run in real `workerd`

## Layout

```
src/
  index.ts          Worker entry: middleware chain + route mounting
  config/env.ts     Fail-fast config validation (secrets / public / bindings)
  auth/             Better Auth factory + invite provisioning
  db/               Drizzle client + Better Auth schema
  middleware/       request-id, logging, error, auth, authorize, validate
  routes/           health, version, languages, admin (invite)
  lib/              logger, errors, crypto, static languages
  types/            Bindings + Hono Variables
drizzle/            Generated SQL migrations (also wrangler migrations_dir)
test/               health, cors, error, auth-middleware, session
docs/               ARCHITECTURE.md, CONTRACT-MISMATCHES.md
```

## Environment variables

| Name                  | Kind    | Where set                                   | Notes |
| --------------------- | ------- | ------------------------------------------- | ----- |
| `APP_ENV`             | public  | `wrangler.jsonc` → `vars`                   | `development` \| `staging` \| `production` |
| `PORTAL_ORIGIN`       | public  | `wrangler.jsonc` → `vars`                   | Exact portal origin, e.g. `https://portal.trackdub.com`. Drives CORS + trustedOrigins. |
| `BETTER_AUTH_SECRET`  | secret  | `.dev.vars` (local) / `wrangler secret put` | ≥16 chars. `openssl rand -base64 32`. Signs sessions. |
| `ADMIN_API_TOKEN`     | secret  | `.dev.vars` (local) / `wrangler secret put` | ≥16 chars. Guards the invite endpoint. |
| `DB`                  | binding | `wrangler.jsonc` → `d1_databases`           | D1 database. |
| `MEDIA`               | binding | `wrangler.jsonc` → `r2_buckets`             | R2 bucket for `/api/dubs/upload` (and later download outputs). |

Config is validated on first request (`src/config/env.ts`); anything missing or
malformed fails fast with a clear, secret-free error.

Copy `.dev.vars.example` → `.dev.vars` for local development.

## Develop

```bash
npm install
cp .dev.vars.example .dev.vars   # then fill in secrets
npm run db:generate              # regenerate migrations after schema changes
npm run db:migrate:local         # apply migrations to local D1
npm run dev                      # wrangler dev
npm test                         # vitest (workerd)
npm run typecheck                # tsc --noEmit
```

## Deploy

1. **Create the D1 database** and paste its id into `wrangler.jsonc`
   (`d1_databases[0].database_id`):

   ```bash
   npx wrangler d1 create trackdub_api
   ```

2. **Create the R2 bucket** referenced by the `MEDIA` binding:

   ```bash
   npx wrangler r2 bucket create trackdub-media
   ```

3. **Set secrets** for the deployed Worker:

   ```bash
   npx wrangler secret put BETTER_AUTH_SECRET
   npx wrangler secret put ADMIN_API_TOKEN
   ```

4. **Apply migrations** to the remote D1:

   ```bash
   npm run db:migrate:remote
   ```

5. **Deploy** and attach the custom domain (`api.trackdub.com` is configured as a
   `custom_domain` route in `wrangler.jsonc`):

   ```bash
   npm run deploy
   ```

6. **Verify:**

    ```bash
    curl https://api.trackdub.com/api/health      # 200 { status: "healthy", ... }, or 503 { status: "degraded", ... } when D1 is down
    curl https://api.trackdub.com/api/health/ready # 200 { status: "ready" }, or 503 { status: "unavailable", ... } when D1 is down
    curl https://api.trackdub.com/api/version     # 200, build metadata from the CF_VERSION_METADATA binding ("unknown" locally)
    ```

## Bootstrap the first user

Registration is invite-only, so seed the first operator with the admin token.
Because PR1 ships no email provider, pass an initial `password` to seed directly
(subsequent real invites omit `password` and use the reset flow):

```bash
curl -X POST https://api.trackdub.com/api/admin/invite \
  -H "Authorization: Bearer $ADMIN_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"email":"you@trackdub.com","name":"You","password":"a-strong-initial-password"}'
```

Then sign in from the portal. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
for the auth flow and [docs/CONTRACT-MISMATCHES.md](docs/CONTRACT-MISMATCHES.md)
for differences discovered against the portal contract.
