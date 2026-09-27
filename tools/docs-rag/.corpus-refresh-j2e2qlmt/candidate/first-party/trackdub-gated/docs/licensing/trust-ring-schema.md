# Trust ring & revocation feed — file formats and operations

This document describes the two files a Release build of `Trackdub.App.Avalonia`
requires next to its executable, how they're produced, and how key rotation and
revocation work. The loading code is
[`ProductionLicensingBootstrap.cs`](../../src/Trackdub.App.Avalonia/Licensing/ProductionLicensingBootstrap.cs);
the parsing/validation code is
[`TrustRingConfiguration.cs`](../../src/Trackdub.App.Avalonia/Licensing/TrustRingConfiguration.cs)
and
[`RevocationConfiguration.cs`](../../src/Trackdub.App.Avalonia/Licensing/RevocationConfiguration.cs).
The generation/signing tool is
[`tools/Trackdub.Licensing.Tooling`](../../tools/Trackdub.Licensing.Tooling).

A Debug build never reads these files — it uses
`TrustRingConfiguration.Development()` unconditionally. Only a Release build
(or a test that calls `ProductionLicensingBootstrap.Resolve` directly) takes
this path.

## Why two files, and why this shape

License tokens are signed with ES256 (ECDSA P-256 + SHA-256 — see core's
`LicenseTokenValidator`). The **trust ring** answers "which public key(s) does
this build trust, and is this a production or development deployment lane".
It is *not* a secret — only public keys ever go in it, and it ships inside the
app bundle.

The **revocation feed** answers "has a previously-trusted key been
compromised or retired since this build was cut". It's signed separately, by
an offline "revocation root" key that never signs license tokens themselves —
only revocation lists. That separation means the revocation feed can be
re-issued (e.g. weekly, or on demand) and redistributed without re-signing or
re-shipping the trust ring itself.

Both files are loaded from `AppContext.BaseDirectory` (i.e., next to the
executable) using the fixed names `trackdub.trust.json` and
`trackdub.revocation.json` — see
`ProductionLicensingBootstrap.TrustRingFileName` /
`.RevocationFileName`. There is no environment-variable override; this is
deliberate (see `App.axaml.cs`'s comment on `SelectLicensingConfiguration`) —
end users cannot point a Release build at a different trust ring.

## `trackdub.trust.json`

```jsonc
{
  "IsProduction": true,
  "CurrentKeyId": "prod-2026-07",
  "Issuer": "https://activate.trackdub.com",
  "Audience": "trackdub-license-prod",
  "ActivationEndpoint": "https://activate.trackdub.com/activate",
  "OfflineRevocationRootPublicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n",
  "Keys": [
    {
      "KeyId": "prod-2026-07",
      "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n",
      "IsDevelopmentOnly": false,
      "NotBefore": "2026-07-01T00:00:00+00:00",
      "NotAfter": null
    }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `IsProduction` | If `true`, `ProductionLicensePolicyInitializer` rejects dev-unlimited tokens, and a revocation feed becomes mandatory (see below). |
| `CurrentKeyId` | Which key in `Keys` newly-issued tokens are expected to use. Must match a `KeyId` in `Keys`. |
| `Issuer` / `Audience` / `ActivationEndpoint` | Top-level defaults; a key entry can override any of them individually (per-key `Issuer`/`Audience`/`ActivationEndpoint`, all optional — omit to inherit the top-level value). The bundled `add-key` tool doesn't expose per-key overrides; hand-edit the JSON if you ever need one. |
| `OfflineRevocationRootPublicKeyPem` | The public half of the revocation-signing key. Required whenever `IsProduction` is `true`. |
| `Keys[].KeyId` | Stable identifier embedded in issued license tokens (`kid`-equivalent). Never reuse a retired key's id for a different key. |
| `Keys[].PublicKeyPem` | ECDSA P-256 public key, PEM-encoded (`ExportSubjectPublicKeyInfoPem` output). |
| `Keys[].IsDevelopmentOnly` | Must be `false` for every key in a production ring — `TrustRingConfiguration.Validate()` rejects a production ring containing a dev-only key. |
| `Keys[].NotBefore` / `NotAfter` | Validity window. `NotBefore` is required for a non-dev-only key; `NotAfter` is optional (omit for "no planned expiry"). |

Loaded by `TrustRingConfiguration.LoadFromFile`/`.FromJson`. Structural
validation (missing fields, unknown `CurrentKeyId`) happens there and throws
`InvalidDataException`. Semantic validation (production ring with a dev-only
key, missing offline revocation root when production) happens in
`TrustRingConfiguration.Validate()`, invoked by
`DesktopLicenseSignatureTrustStore`'s constructor — that's the check that
actually gates app startup, not the loader itself.

## `trackdub.revocation.json`

```jsonc
{
  "Version": 1,
  "IssuedAt": "2026-07-30T00:00:00+00:00",
  "ExpiresAt": "2026-08-06T00:00:00+00:00",
  "SignerKeyId": "prod-2026-07",
  "RevokedKeyIds": ["prod-2026-01"],
  "Signature": "base64-ecdsa-signature",
  "CachedFromKeyId": null
}
```

| Field | Meaning |
| --- | --- |
| `Version` | Schema version, currently always `1`. |
| `IssuedAt` / `ExpiresAt` | Validity window for this specific revocation *list* (separate from any individual trust-ring key's window). `DesktopLicenseSignatureTrustStore` fails closed — treats every key as untrusted — once `ExpiresAt` passes, under a production ring. This is what forces the feed to be refreshed periodically rather than shipped once and forgotten. |
| `SignerKeyId` | Informational label for which offline root key signed this feed — not cryptographically checked against anything, just an audit trail. |
| `RevokedKeyIds` | Trust-ring `KeyId`s that must no longer be trusted, even if still inside their `NotBefore`/`NotAfter` window. |
| `Signature` | Base64 ECDSA signature over the canonical payload (below), verified against `TrustRingConfiguration.OfflineRevocationRootPublicKeyPem`. |
| `CachedFromKeyId` | Optional bookkeeping field for feed-distribution tooling; not interpreted by this codebase. |

The **canonical payload** that gets signed is defined by
`RevocationConfiguration.GetCanonicalPayloadBytes()`: a UTF-8 JSON object with
keys `Version`, `IssuedAt`, `ExpiresAt`, `SignerKeyId`, `RevokedKeyIds` (sorted
ordinally), serialized via `System.Text.Json` in that exact property order.
Anything that re-signs a revocation feed **must** produce byte-identical
output to this method, or the signature won't verify — this is why
`sign-revocation` links the real `RevocationConfiguration.cs` file rather than
reimplementing the canonicalization.

## Where the private keys live

Nothing under this repo, ever:

- The **license-signing private key** (the one that actually issues license
  tokens to customers) doesn't appear anywhere in this document or tool —
  it's used by whatever activation/issuance system signs tokens, not by the
  desktop app or this repo at all. Only its public counterpart goes in
  `trust.json`.
- The **offline revocation-root private key** — generated by
  `generate-key`, immediately moved off this machine to secure storage
  (a secrets manager or an offline/air-gapped vault; pick one and record
  where, this repo makes no assumption). Used only to run `sign-revocation`
  when the revocation feed needs updating.

## Key rotation procedure

1. `generate-key <new-key-id> <output-dir>` — produces the new keypair.
   Move the `.private.pem` off-machine immediately.
2. `add-key trackdub.trust.json --key-id <new-key-id> --public-key <new .public.pem> ... --production --not-before <now-ish>` — adds the new key *without* `--make-current` yet, so tokens already issued under the old key keep validating while the new key becomes available.
3. Once issuance has cut over to signing with the new key, run `add-key` again with `--make-current` to flip `CurrentKeyId`.
4. Keep the old key's entry in `trust.json` (don't delete it) until its `NotAfter` passes or every token signed with it has expired — whichever is later.
5. If the *old* key needs to stop being trusted immediately (compromise, not just routine rotation), that's what `sign-revocation --revoked <old-key-id>` is for — see below.

## Revocation procedure

1. `sign-revocation trackdub.revocation.json --signer-key-id <offline-root-key-id> --private-key <offline-root .private.pem> --revoked <comma-separated key ids> --expires-in-hours <n>` — produces a freshly-signed feed.
2. Redistribute `trackdub.revocation.json` to Release installs (mechanism is outside this repo's scope — this doc only covers the file format and signing step).
3. Because the feed has its own `ExpiresAt`, plan to re-run step 1 before it lapses even if the revoked-key-id list hasn't changed — an expired feed fails closed (§ above), which is a safety property, not a bug, but it does mean "no revocations right now" still needs a periodically-refreshed empty-list feed under a production ring.

## Packaging

`trackdub.trust.json` (public keys only — safe to ship) is checked into
[`src/Trackdub.App.Avalonia/Licensing/production/`](../../src/Trackdub.App.Avalonia/Licensing/production/)
and copied to the Release output directory by
`Trackdub.App.Avalonia.csproj`. `trackdub.revocation.json` is **not** checked
in — it expires on a schedule shorter than a typical release cadence, so it's
regenerated and deployed to installs independently of a build (mechanism
outside this repo's scope; see "Revocation procedure" above).

**A plain `dotnet build`/`dotnet publish -c Release` output is therefore not
by itself a bootable artifact** — it's missing `trackdub.revocation.json`
until whoever assembles the final installer/release package adds it
(`sign-revocation`'s output, placed next to the built executable). This is
intentional (see above), not an oversight, but it means: don't hand a bare
Release build folder to anyone expecting to run it — `ProductionLicensingBootstrap`
will fail fast with a clear "revocation feed required" error rather than a
silent or confusing crash, but it will still fail.

**This packaging step now exists**:
[`tools/release/package-release.ps1`](../../tools/release/package-release.ps1)
publishes, adds the caller-supplied signed revocation feed, and gates on
`verify-release` (see below) before producing a zip — plus
[`.github/workflows/release.yml`](../../.github/workflows/release.yml) runs
the same thing from a `TRACKDUB_REVOCATION_FEED_JSON` repository secret (the
offline revocation-root *private key* never touches CI; the secret only ever
holds an already-signed feed).

## `verify-release` — the enforcement point for a shippable trust ring

`tools/Trackdub.Licensing.Tooling verify-release <published-output-dir>` is
the hard gate `package-release.ps1` runs before zipping anything. It
re-derives the same verdict `ProductionLicensingBootstrap` and
`DesktopLicenseSignatureTrustStore` reach at app startup — trust ring
present, `IsProduction`, full `Validate()` (including the
`REPLACE_BEFORE_PRODUCTION_RELEASE` placeholder checks below), revocation
present, `IsProduction: true`, full `Validate()` (including the
root key, and it isn't expiring within `--min-validity-hours` (default 72) —
but at packaging time, not at a customer's first launch.

Concretely, this is what currently stands between the checked-in
[`trackdub.trust.json`](../../src/Trackdub.App.Avalonia/Licensing/production/trackdub.trust.json)
and a real release: its `Issuer`/`Audience`/`ActivationEndpoint` (see the
field table above) are still `REPLACE_BEFORE_PRODUCTION_RELEASE:...`
placeholders, and `verify-release` will refuse to package until real values —
owned by the activation-backend phase (deliberately excluded from this repo;
see `docs/plans/trackdub-gated-split-manifest.md`), not this repo — replace
them. That refusal is the intended behavior, not a bug to work around. There
is no provisioned activation endpoint yet to substitute, so don't invent one
here even temporarily — `TrustRingConfiguration.Validate()`'s placeholder
check exists specifically to catch that mistake.
