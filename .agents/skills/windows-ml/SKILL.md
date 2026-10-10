---
name: windows-ml
description: Windows ML (WinML, Microsoft.Windows.AI.MachineLearning) guidance in three evidence tiers. Tier 1 is canonical Microsoft Learn documentation. Tier 2 is ONNX Runtime / ONNX Runtime GenAI source and NuGet package facts. Tier 3 is Trackdub's own dated observations. Covers model setup, all Windows ML execution providers (CPU, DirectML, OpenVINO, QNN, VitisAI, MIGraphX, NvTensorRtRtx, experimental WebGPU), deployment, ExecutionProviderCatalog install and registration, OrtEpDevice selection, EP-context compilation and caching, ONNX Runtime GenAI on Windows ML, and diagnostics. Use whenever work touches Windows ML or WinML, ExecutionProviderCatalog, EnsureReadyAsync/TryRegister, GetEpDevices, onnxruntime.dll conflicts, Microsoft.ML.OnnxRuntimeGenAI.WinML, DirectML availability, or a Windows ML / ONNX Runtime upgrade, even when the user only says "DirectML is broken" or "why doesn't the NPU show up".
---

# Windows ML

Windows ML is ONNX Runtime plus an execution-provider (EP) catalog. Inference uses ordinary ONNX Runtime APIs. The catalog acquires optional vendor EPs and registers them with ONNX Runtime. CPU and DirectML are included; the vendor EPs are acquired separately. ([Overview](https://learn.microsoft.com/windows/ai/new-windows-ml/overview), [acceleration](https://learn.microsoft.com/windows/ai/new-windows-ml/accelerate-ai-models))

## Evidence tiers: read this first

This skill keeps three kinds of knowledge apart. Upstream docs can be wrong or silent, and a local observation can come from a misconfiguration rather than the platform. So never let a lower tier pass as a higher one.

| Tier | Folder | What may go in | How to use it |
| --- | --- | --- | --- |
| 1. Canon | [`canon/`](canon/) | Only what a `learn.microsoft.com` page states, each claim linked and dated. | Platform truth. If Learn is silent, say "unresolved"; never fill the gap from another tier. |
| 2. Upstream source | [`upstream/`](upstream/) | What ONNX Runtime / ONNX Runtime GenAI source code, tests or NuGet package contents show. Each claim names its repository, file and commit, or its package and version. | Implementation detail Learn does not document. True for the cited version only. Re-check after any version change. |
| 3. Trackdub observations | [`trackdub/`](trackdub/) | Things seen on Trackdub machines, each with its date, setup, observer, reproduction status and suspected-cause confidence. | Leads to re-test, not facts. Never quote one as platform behaviour. Before code relies on an observation, reproduce it under the current versions. Record whether it held. |

Rules when using and when editing this skill:

- **State the tier** whenever you rely on a claim, for example "Learn says…", "ORT source at <commit> shows…" or "observed once on 2026-10-07…".
- **Promote only with evidence.** An observation moves to Tier 2 or 1 only when source or Microsoft documentation confirms it. A config mistake that was later fixed gets marked as a misconfiguration, not deleted silently.
- **Contradictions go to the higher tier.** If source or docs contradict an observation, record the contradiction on the observation entry and treat the observation as suspect.
- **Do not copy volatile numbers** (package pins, driver minimums) into tiers 2–3. Point to the file or page that owns them.

## Read by task

| Task | Start with |
| --- | --- |
| Which providers exist, their requirements and limits | [canon/execution-providers.md](canon/execution-providers.md) |
| Per-provider hardware, options, precision gaps, deployment constraints and diagnostic checks | [canon/provider-configuration-coverage.md](canon/provider-configuration-coverage.md) |
| Get a model running: sourcing, CPU path, acceleration, GenAI, compile and cache | [canon/model-setup-and-configuration.md](canon/model-setup-and-configuration.md) |
| Packages, deployment mode, install → register → select lifecycle, with C# recipes | [canon/deployment-and-lifecycle.md](canon/deployment-and-lifecycle.md) |
| Updates, offline devices, version traps, language differences | [canon/structure-and-edge-cases.md](canon/structure-and-edge-cases.md) |
| Download failures, ETW logs, "provider missing" triage | [canon/diagnostics.md](canon/diagnostics.md) |
| How ORT / GenAI behave where Learn is silent (managed binding, registration names, GenAI provider names, package contents) | [upstream/ort-and-genai-source.md](upstream/ort-and-genai-source.md), [upstream/packages.md](upstream/packages.md) |
| What Trackdub has seen, and where its Windows ML code lives | [trackdub/observations.md](trackdub/observations.md), [trackdub/where-things-live.md](trackdub/where-things-live.md) |

## Canonical workflow (Tier 1)

1. Obtain or convert an ONNX model compatible with the ORT version shipped in the chosen Windows ML release.
2. Choose runtime deployment (self-contained or framework-dependent) separately from EP sourcing (catalog, bundled, or hybrid).
3. Establish the CPU inference path first.
4. Find compatible EPs, then install or prepare and register the one you need.
5. Enumerate EP devices, select explicitly, configure session options and create the session. Try device policies only after explicit selection works.
6. Compile and cache hardware-specific artifacts, and validate them before reuse.
7. Use Windows ML diagnostic logs to see what was actually selected and why it failed.

Sources: [get started](https://learn.microsoft.com/windows/ai/new-windows-ml/get-started), [deployment](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app), [install](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers), [select](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers), [compilation](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation), [logs](https://learn.microsoft.com/windows/ai/new-windows-ml/logs).

Cover all providers equally; do not default to NVIDIA-specific configuration. [samples/ConfigureExplicitProvider.cs](samples/ConfigureExplicitProvider.cs) adapts Microsoft's explicit-selection pattern. It only configures options: it does not install an EP, create a session or run a model.

## Restrictions to check first (Tier 1)

- Catalog-acquired hardware EPs require Windows 11 24H2 (build 26100) or later and suitable hardware and drivers. CPU and DirectML have broader OS coverage. ([Providers](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers))
- Model compatibility depends on the ORT version shipped with Windows ML, not just the ONNX file format. ([Models](https://learn.microsoft.com/windows/ai/new-windows-ml/models))
- The MIGraphX Windows ML EP does not currently support GenAI scenarios. ([Providers](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers))
- WebGPU is experimental and needs experimental packages and explicit installation. It is not recommended for performance-critical work. ([WebGPU](https://learn.microsoft.com/windows/ai/new-windows-ml/webgpu-ep))
- The separate Windows ML Runtime API is experimental, unsupported for production or Store publication, and has no C# support. ([Runtime API](https://learn.microsoft.com/windows/ai/new-windows-ml/runtime/overview))
- EP licences are separate from the Windows ML licence and say nothing about a model's rights. ([Third-party notices](https://learn.microsoft.com/windows/ai/new-windows-ml/third-party-notices))

## Looking things up

Use the Microsoft Learn MCP: `microsoft_docs_search`, then `microsoft_docs_fetch`, and `microsoft_code_sample_search` with `language: "csharp"` for API examples. Keep each query to one concept. If MCP is unavailable, use `npx @microsoft/learn-cli search|fetch|code-search` and accept only `learn.microsoft.com` results for Tier 1.

| Topic | Query |
| --- | --- |
| Model compatibility | `Windows ML models ONNX Runtime versions` |
| Provider lifecycle | `Windows ML EnsureReadyAsync RegisterCertifiedAsync TryRegister` |
| Hardware placement | `Windows ML GetEpDevices AppendExecutionProvider device policies` |
| Cache correctness | `Windows ML model compilation EP_NOT_APPLICABLE source hash` |
| Restricted or offline devices | `Windows ML execution provider download issues bring your own` |
| Deployment and updates | `Windows ML self-contained framework-dependent updates` |
| Model preparation | `Windows ML CLI model conversion optimization quantization` |
| Diagnostics | `Windows ML logs ETW rundown execution provider selection` |

For Tier 2, read the source itself, for example with GitHits on `github:microsoft/onnxruntime` or `github:microsoft/onnxruntime-genai` at a pinned commit, or NuGet package contents. Record the commit or package version next to the claim.

Report what the documentation establishes, what source shows, what was observed, and what was actually tested. Documentation or a compiled sample alone does not prove model execution.
