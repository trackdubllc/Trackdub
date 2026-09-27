# Activation service contract

`src/activation/` is a **separate Cloudflare Worker** (`trackdub-activation`,
deployed from `wrangler.activation.jsonc`) from the portal API in this repo.
It handles desktop license activation for `Trackdub-gated`, and exists
because that repo's release gate — `verify-release` — refuses to package a
build while
`src/Trackdub.App.Avalonia/Licensing/production/trackdub.trust.json` still
carries `REPLACE_BEFORE_PRODUCTION_RELEASE` placeholders for `Issuer`,
`Audience`, and `ActivationEndpoint`.

It is deployed as its own Worker, with its own D1 database and its own
`SIGNING_KEY` secret, so that a bug anywhere in the portal Worker's routes can
never reach the key that signs license tokens.

## Why it looks like this: ported from a service that was never deployed

The implementation here is adapted from
`trackdubllc/Trackdub-Monorepo-Archive`'s `services/activation-service/` — a
complete, previously-reviewed Cloudflare Worker that was **never deployed**
(no matching Worker or D1 database existed in the account before this PR).
The SQL for seat accounting (`src/activation/lib/seats.ts`) is ported
verbatim from that source, including its concurrency handling: every
INSERT/UPDATE that claims a seat carries its `COUNT(*) < max_machines`
predicate in the same statement as the write, so two requests racing for the
last seat cannot both succeed.

**One thing was not safe to port verbatim.** The reference implementation's
token claims never included `kid`. The desktop's
`DesktopLicenseSignatureTrustStore.ResolvePublicKeyPem` returns `null`
immediately for a null/empty key id, which means a Release build would fail
to verify signature on every token that service would have issued. Every
token minted here carries `kid` (and `iss`/`aud`) — see
`src/activation/lib/jwt.ts` for the full explanation, including a
JWT-convention subtlety: the desktop's `LicenseTokenParser` reads `kid` from
the **JWT payload**, not the header, so it's emitted in both places.

## Wire contract (desktop-facing)

Verified against `Trackdub-gated/src/Trackdub.App.Avalonia/ViewModels/LicensePanelViewModel.cs`,
which is a finished HTTP client, not a stub — this is not a proposed
contract, it's what already ships:

| Endpoint | Called by shipped desktop? | Request | Success | Errors |
|---|---|---|---|---|
| `POST /activate` | Yes | `{key, fingerprint}` | 200 `{token}` | `invalid_key` 400, `invalid_fingerprint` 400, `key_revoked` 403, `max_activations` 409 `{max}` |
| `POST /deactivate` | Yes | `{key, fingerprint}` | 200 `{status:"released"}` (idempotent) | same as above |
| `POST /reactivate` | No (support/future use) | `{key, old_fingerprint, new_fingerprint}` | 200 `{token}` | above + `activation_not_found` 400/409, `max_activations` **403** (not 409 — an inconsistency inherited from the reference implementation, not introduced here) |
| `POST /status` | No (support/future use) | `{key}` | 200 `{tier, status, activations_used, activations_max, expires_at, dev_unlimited?}` | `invalid_key` 400, `key_revoked` 403 |
| `POST /admin/licenses` | No (operator-only, `Authorization: Bearer <ADMIN_API_TOKEN>`) | `{email, tier?, maxMachines?}` | 201 `{licenseKey, licenseId}` | portal-style `{error:{code,message,requestId}}` |

`fingerprint` is a 64-char lowercase hex SHA-256, produced by
`Trackdub/src/Trackdub.Licensing/HardwareFingerprintProvider.cs`.

**Error envelope is flat** — `{"error": "max_activations", "max": 2}` — not
the portal's `{error:{code,message,requestId}}`. This is deliberate: the
shipped desktop client parses the flat shape, and changing it would only help
builds released after the change while breaking every installed one. The
`/admin/licenses` route is the one exception, since no desktop code calls it.

## Deferred: purchase webhooks

The reference implementation also had `/webhook/purchase` (Stripe + Lemon
Squeezy, HMAC-verified, idempotent via a `purchase_events` table) to mint
license keys automatically on payment. **Not ported in this PR** — needs
payment-provider credentials and a license-delivery target decided first.
Until then, `POST /admin/licenses` is how a key gets issued: an operator
confirms payment out of band and mints.

## Runbook: keys, D1, domains

**The private key never lives in any repo.** It exists on your machine only
long enough to (a) hand its public half to `add-key`, and (b) pipe its
private half into `wrangler secret put`; delete the local file afterward and
keep a copy only in whatever secrets manager you use.

### 1. Generate keys (in `Trackdub-gated`)

```bash
mkdir -p ~/trackdub-keys-tmp
dotnet run --project tools/Trackdub.Licensing.Tooling -- \
  generate-key trackdub-prod-2026-08b ~/trackdub-keys-tmp
dotnet run --project tools/Trackdub.Licensing.Tooling -- \
  generate-key trackdub-staging-2026-08b ~/trackdub-keys-tmp
```

Production and staging must use **different** keys — a staging-signed token
must never verify against the production trust ring.

### 2. Upload secrets (in this repo)

```bash
wrangler secret put SIGNING_KEY --config wrangler.activation.jsonc \
  < ~/trackdub-keys-tmp/trackdub-prod-2026-08b.private.pem
wrangler secret put ADMIN_API_TOKEN --config wrangler.activation.jsonc
# paste output of: openssl rand -hex 32

wrangler secret put SIGNING_KEY --config wrangler.activation.staging.jsonc \
  < ~/trackdub-keys-tmp/trackdub-staging-2026-08b.private.pem
wrangler secret put ADMIN_API_TOKEN --config wrangler.activation.staging.jsonc
```

`SIGNING_KEY_ID` is **not** a secret — it's a public identifier that appears
in every issued token's plaintext claims. It's set as a plain `vars` entry in
`wrangler.activation.jsonc` / `wrangler.activation.staging.jsonc`; update it
there (and redeploy) as the last step of key rotation, once the trust ring's
`CurrentKeyId` has already been flipped to the new key.

### 3. Apply migrations

D1 databases already provisioned: `trackdub_activation`
(`f95337b1-aff5-455d-a0f9-3293211b1727`) and `trackdub_activation_staging`
(`d694b5d9-3c04-4534-a9cd-6892967fb5ee`).

```bash
npm run db:migrate:activation:remote
npm run db:migrate:activation:staging:remote
```

### 4. Deploy

This is what actually creates the `activate.trackdub.com` /
`activate.trackdub.dev` DNS records and TLS certs — Workers Custom Domains
(`custom_domain: true` in the `routes` config) provision both automatically
on deploy, given an active zone with no conflicting CNAME already on that
hostname.

```bash
npm run deploy:activation
npm run deploy:activation:staging
```

**This ordering (deploy before trust ring) is only correct for a first-time
bootstrap, where no shipped desktop build trusts anything yet — there's no
live key to protect.** For a real future rotation of an already-trusted key,
reverse it: run step 5's `add-key` first (adding the new key without
`--make-current` and shipping a new desktop release containing it), and only
flip `SIGNING_KEY_ID` + redeploy here once that release has actually reached
users — otherwise the server would start signing tokens with a `kid` no
installed build can resolve yet. Note that even doing step 5 immediately
before step 4 doesn't fully protect a live rotation on its own: `trust.json`
is baked into each desktop build at package time, not fetched live, so the
real lead time that matters is "a new desktop release has shipped and been
adopted," not the few seconds between these two commands.

### 5. Add the public key to the desktop trust ring (in `Trackdub-gated`)

```bash
dotnet run --project tools/Trackdub.Licensing.Tooling -- add-key \
  src/Trackdub.App.Avalonia/Licensing/production/trackdub.trust.json \
  --key-id trackdub-prod-2026-08b \
  --public-key ~/trackdub-keys-tmp/trackdub-prod-2026-08b.public.pem \
  --issuer https://activate.trackdub.com \
  --audience trackdub-license-prod \
  --activation-endpoint https://activate.trackdub.com/activate \
  --production --not-before $(date -u +%Y-%m-%dT%H:%M:%SZ) --make-current
```

Keep any prior key's entry in the ring — don't delete a key that may have
signed a still-valid token.

### 6. Shred local private keys

```bash
shred -u ~/trackdub-keys-tmp/*.private.pem 2>/dev/null || rm -P ~/trackdub-keys-tmp/*.private.pem
```

### Verifying end to end

```bash
curl -sS -X POST https://activate.trackdub.com/admin/licenses \
  -H "Authorization: Bearer <ADMIN_API_TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"email":"you@example.com"}'
# -> {"licenseKey": "TDUB-...", "licenseId": "..."}
```

Then, with `Trackdub-gated` built in Release configuration, set
`TRACKDUB_ACTIVATION_URL=https://activate.trackdub.dev` (staging) and
activate through Settings → License. Reaching Pro tier confirms the full
chain: Worker signs with `kid` → desktop resolves that `kid` through the real
trust ring → signature verifies. No unit test on either side covers that
chain alone — this manual check is the actual gate before pointing
production traffic (and `verify-release`) at the `.com` deployment.
