# GitHub Actions Workflows

CI/CD lives in `.github/workflows/`. Windows jobs use self-hosted runners; Linux jobs use `self-hosted`.

**Workflows do not auto-run on push or pull request.** Start them manually or from PR comments:

| Command (PR comment) | Workflow |
|----------------------|----------|
| `/ci` | Full CI (format + Windows/Linux build/test) |
| `/oc` or `/opencode` | OpenCode bot |

Manual dispatch still works:

```powershell
gh workflow run ci.yml
gh workflow run release.yml -f tag=v1.2.3
gh workflow run cursor-code-review.yml -f pull_request_number=123
gh workflow run opencode.yml -f prompt="Summarize recent pipeline changes"
```

## Active workflows

### CI (`ci.yml`)

- **Trigger:** PR comment `/ci`, or manual (`workflow_dispatch`)
- **Jobs:**
  - **Verify Code Format** (self-hosted): `dotnet format Trackdub.sln --verify-no-changes`
  - **Build & Test (Windows):** restore/build/test `Trackdub.sln` (Release, `-m:1`)
  - **Build & Test (Linux):** restore/build/test `Trackdub.Avalonia.slnf` on `net10.0`; tests run per project via `scripts/ci/run-avslnf-tests-sequential.sh`
- **Timeout:** 45 minutes per build matrix leg

### Dependabot Auto-Merge (`dependabot-auto-merge.yml`)

- **Trigger:** Automatically runs on `pull_request` when Dependabot opens or updates a PR
- **Jobs:**
  - **Auto-merge Dependabot PR:** fetches Dependabot metadata, approves the PR, and enables auto-merge with `--squash` via `gh` CLI. It ensures that once all required status checks/tests pass on the PR, the PR is automatically and safely merged.

### Release (`release.yml`)

- **Trigger:** Manual (`workflow_dispatch`, required `tag` input e.g. `v1.2.3`)
- **Jobs:** Solution tests, Windows release build, Linux/macOS-style Unix publish matrix, GitHub Release upload

### API deploy (`api-deploy.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Runs:** self-hosted
- **Tasks:** Docker build, ECR push, ECS task render + deploy

### Model manifest audit (`model-audit.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Runs:** self-hosted
- **Tasks:** `tools/ci/audit-bundled-model-manifest.py`

### Cursor code review (`cursor-code-review.yml`)

- **Trigger:** Manual (`workflow_dispatch`, required `pull_request_number`)
- **Runs:** `ubuntu-latest`
- **Tasks:** Deno 2 runs `tools/cursor-sdk-agent` via `@cursor/sdk`; posts/updates a single PR comment
- **Secret:** `CURSOR_API_KEY` (repository secret)

### OpenCode review (`opencode-review.yml`)

- **Trigger:** Manual (`workflow_dispatch`, required `pull_request_number`)
- **Runs:** self-hosted
- **Tasks:** `anomalyco/opencode/github` reviews the PR via OpenRouter (comment-only prompt; must not commit/push)
- **Secret:** `OPENROUTER_API_KEY` (repository secret); uses `GITHUB_TOKEN` for GitHub API

### OpenCode on demand (`opencode.yml`)

- **Trigger:** PR comment `/oc` or `/opencode`, or manual (`workflow_dispatch` with `prompt`)
- **Runs:** ubuntu-latest
- **Tasks:** Runs OpenCode with the supplied prompt

### TRT RTX smoke (`trt-rtx-smoke.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Runs:** self-hosted Windows when `TRACKDUB_TRT_RTX_SMOKE == 'true'`

### Frontend build (`frontend-build.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Tasks:** `pnpm install --frozen-lockfile` + Vite production build for `frontend/`

### CodeQL Advanced (`codeql.yml`)

- **Trigger:** Push/PR to `main`, weekly schedule (Mon 01:42 UTC), manual (`workflow_dispatch`)
- **Runs:** `ubuntu-latest` (actions, JS/TS, Python — `build-mode: none`); `windows-latest` (C# manual `Trackdub.sln` build)
- **Tasks:** Advanced CodeQL with `security-extended,security-and-quality`; path config in `.github/codeql/codeql-config.yml`
- **Important:** Only canonical CodeQL workflow for this repo. Disable GitHub default CodeQL (org `trackdubllc-org-config-1` or repo settings) to avoid duplicate dynamic `CodeQL` runs. See `docs/internal/codeql-advanced-setup.md`.

```powershell
gh workflow run codeql.yml
gh run list --workflow=codeql.yml --limit 3
```

### Code coverage (`code-coverage.yml`)

- **Trigger:** Push/PR to `main`, manual (`workflow_dispatch`)
- **Runs:** `ubuntu-latest`
- **Tasks:** Coverlet on `Trackdub.Avalonia.slnf`, ReportGenerator merge, `actions/upload-code-coverage`, PR comment

## Secrets (deploy + review)

| Secret | Purpose |
|--------|---------|
| `AWS_DEPLOY_ROLE_ARN` | OIDC role for API deploy |
| `ECS_EXECUTION_ROLE_ARN` / `ECS_TASK_ROLE_ARN` | ECS task definition |
| `AWS_ACCOUNT_ID` / `EFS_FILE_SYSTEM_ID` | Task definition substitution |
| `CURSOR_API_KEY` | Cursor SDK PR review (`cursor-code-review.yml`) |
| `OPENROUTER_API_KEY` | OpenCode PR review (`opencode-review.yml`, `opencode.yml`) |

## Local parity

```powershell
dotnet format Trackdub.sln --verify-no-changes
dotnet build Trackdub.sln -c Release -m:1
dotnet test Trackdub.sln -c Release --no-build -m:1
dotnet build Trackdub.Avalonia.slnf -c Release -f net10.0 -m:1
./scripts/ci/run-avslnf-tests-sequential.sh Trackdub.Avalonia.slnf "--framework net10.0"
deno task validate
```

Last updated: 2026-07-11
