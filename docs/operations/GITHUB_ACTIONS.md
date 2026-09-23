# GitHub Actions Workflows

CI/CD lives in `.github/workflows/`. Windows jobs use self-hosted runners; Linux jobs use `self-hosted`.

`ci.yml`, `codeql.yml`, `model-audit.yml`, `dependabot-auto-merge.yml`, and `opencode-review.yml` run automatically on their triggers. Everything else is manual (`workflow_dispatch`) or PR-comment triggered:

| Command (PR comment) | Workflow |
|----------------------|----------|
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

- **Trigger:** Push/PR to `main`, or manual (`workflow_dispatch`)
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

- **Trigger:** push/PR to `main` touching `src/Trackdub.Inference/Runtime/ModelManifest/**`, `tools/ci/**`, or the workflow itself; weekly schedule (Mon 06:00 UTC); manual (`workflow_dispatch`)
- **Runs:** self-hosted
- **Tasks:** `tools/ci/audit-bundled-model-manifest.py`

### Cursor code review (`cursor-code-review.yml`)

- **Trigger:** Manual (`workflow_dispatch`, required `pull_request_number`)
- **Runs:** `ubuntu-latest`
- **Tasks:** Deno 2 runs `tools/cursor-sdk-agent` via `@cursor/sdk`; posts/updates a single PR comment
- **Secret:** `CURSOR_API_KEY` (repository secret)

### OpenCode review (`opencode-review.yml`)

- **Trigger:** `pull_request` (opened/reopened/synchronize/ready_for_review)
- **Runs:** `ubuntu-latest`
- **Tasks:** calls the `tonythethompson/opencode-review-threads` reusable `opencode-review.yml` (`/review-pr`); posts one structured GitHub review (summary body plus inline resolvable threads) as `opencode-agent[bot]`. After a submitted review, later pushes only diff commits since that review, so unchanged findings are not re-raised
- **Gate (enforced inside the called reusable workflow, not in this repo):** same-repository PRs only, so fork PRs receive no secrets; author must be `OWNER`/`MEMBER`/`COLLABORATOR`/`CONTRIBUTOR`, non-draft, and not a bot; a `model` input is an explicit opt-in bypass
- **Secrets:** `OPENCODE_API_KEY` (zen: probe chain) and `CLOUDFLARE_ACCOUNT_ID` + `CLOUDFLARE_API_TOKEN` (cf: Workers AI probe chain)
- **Requirement:** the OpenCode GitHub App must be installed on the repo for the `opencode-agent[bot]` token exchange; otherwise reviews fail or need `use-github-token: true` (posts as `github-actions[bot]`)

### OpenCode on demand (`opencode.yml`)

- **Trigger:** issue comments and pull request review comments (`created`), or manual (`workflow_dispatch` with `prompt`)
- **Runs:** `ubuntu-latest`
- **Tasks:** calls the `tonythethompson/opencode-review-threads` reusable `opencode-bot.yml` with the comment or supplied prompt; replies as `opencode-agent[bot]`
- **Gate (enforced inside the called reusable workflow; the caller also pre-filters commenter association):** non-bot commenters with `OWNER`/`MEMBER`/`COLLABORATOR`/`CONTRIBUTOR` association whose comment starts with or contains ` /oc` or ` /opencode`; `workflow_dispatch` requires `prompt`

### TRT RTX smoke (`trt-rtx-smoke.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Runs:** self-hosted Windows when `TRACKDUB_TRT_RTX_SMOKE == 'true'`
- **Steps:** restore, fetch the TensorRT RTX EP plugin (`tools/dev/Fetch-TrtRtxEp.ps1`), build the benchmarks, download the starter-pack models, then run `Trackdub.Benchmarks.DevHost --scope trt-rtx-smoke --provider trt-rtx`.
- **Model download:** a "Download starter-pack models" step runs `trackdub models download` for every target in `TrtRtxSmokeCatalog.StarterPackTurboGpu` (with variants `gpu-int4`, `quantized`, and `fp16` where the catalog defines them) before the smoke run. Without it the resolver skips every target. The download step and the smoke step share one model cache via job-level `TRACKDUB_CACHE_ROOT` and `TRACKDUB_MODEL_CACHE` (kept consistent so `TRACKDUB_MODEL_CACHE == TRACKDUB_CACHE_ROOT/model-cache`).
- **Failure surfacing:** the job no longer uses `continue-on-error`, so a non-zero smoke exit fails the run. The benchmark exits non-zero when every target is skipped ("TRT RTX smoke did not run any targets (all skipped)"), so a run that downloads nothing or skips everything now turns red instead of reporting green.

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
| `OPENCODE_API_KEY` | OpenCode review/bot zen: probe chain (`opencode-review.yml`, `opencode.yml`) |
| `CLOUDFLARE_ACCOUNT_ID` / `CLOUDFLARE_API_TOKEN` | OpenCode review/bot cf: Workers AI probe chain |

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
