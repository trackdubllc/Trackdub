# AGENTS.md

## Cursor Cloud specific instructions

Cloudflare Workers API. Local: `bun install --frozen-lockfile`, then `bun run typecheck` / `bun run dev` (`wrangler dev`).

- Secrets for local/cloud agents: `BETTER_AUTH_SECRET`, `ADMIN_API_TOKEN` (see `.dev.vars`; use Cursor Secrets tab for cloud, do not commit).
- Staging/activation workers use separate wrangler configs (`wrangler.activation*.jsonc`).
- Prefer `bun install --frozen-lockfile` over `bun install` so the lockfile stays authoritative.
- `zod` is pinned via `overrides` in `package.json` so `@modelcontextprotocol/sdk` resolves the app's `zod` 3.x instead of a nested zod 4; other deps that require `zod@4.x` (`@modelcontextprotocol/client`, `@modelcontextprotocol/server`, `@modelcontextprotocol/core`, `better-auth`, `@cloudflare/vitest-pool-workers`) keep their own nested copy — don't widen the override to the whole tree.
