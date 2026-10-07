# Sidecar wire protocol (normative, v1)

Transport: JSON-lines on stdin/stdout, one request per line, one response per
line, strictly ordered (no interleaving). Every response carries
`protocolVersion`; any other value refuses to serve.

## Requests (C# host / supervisor → worker)

```jsonc
{ "id": "req-1", "op": "health" }
{ "id": "req-2", "op": "load", "plan": {
    "model": "<repo id or local path>",
    "providers": ["CUDA", "CPU"],       // ordered, planner-approved
    "requirePreferred": false,          // honor hard-pin: no silent fallback
    "voicePromptPath": "<reference voice wav, optional>" } }
{ "id": "req-3", "op": "infer", "inputs": {
    "input_ids": { "dtype": "int64", "shape": [1, 3], "data": "<base64>" } } }
```

`id` may be null on responses to unparseable lines (nothing to echo).

## Responses (worker → host)

```jsonc
{ "id": "req-1", "status": "alive", "modelLoaded": false,
  "activeProvider": "none", "protocolVersion": 1 }
{ "id": "req-2", "status": "loaded", "activeProvider": "cuda",
  "model": "<resolved>", "protocolVersion": 1 }
{ "id": "req-3", "status": "ok",
  "outputs": { "audio": { "dtype": "int16", "shape": [95616], "data": "<base64>" } },
  "sampleRate": 24000, "protocolVersion": 1 }
{ "id": null, "status": "error", "reason": "invalid-json",
  "detail": "<parse error>", "protocolVersion": 1 }
```

## Status and reason vocabulary (closed sets — do not invent more)

- `status`: `alive` | `loaded` | `ok` | `error`
- `reason`: `invalid-json` | `unknown-op` | `bad-plan` | `bad-inputs` |
  `dependency-missing` | `model-not-found` | `load-failed` |
  `no-model-loaded` | `infer-failed` | `load-not-implemented`

## Rules

1. A malformed line never kills the loop: answer `invalid-json`, keep serving.
2. Readiness keys only on real state: `loaded` requires a committed session /
   model; `ok` requires it to have produced output. Placeholders answer errors.
3. The worker executes the load plan; it never selects models or providers.
4. `activeProvider` reports what actually settled — never "cuda" when the
   answer was CPU.
