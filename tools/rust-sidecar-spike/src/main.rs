// SKETCH — load-once / serve-forever inference sidecar.
// Not compiled in CI. Illustrates the seam; details TBD in the spike.
//
// Protocol: JSON-lines on stdin/stdout.
//   {"id":"..","op":"load","plan":{...}} -> {"id":"..","status":"loaded",...}
//   (plan is planner-approved: integrity-qualified model + ordered providers;
//    the sidecar executes it, never selects models/providers itself)
//   {"id":"..","op":"infer","inputs":{...}}               -> {"id":"..","status":"ok",...}
//   {"id":"..","op":"health"}                             -> {"id":"..","status":"alive",...}

use std::collections::HashMap;
use std::io::{BufRead, Write};

fn main() -> anyhow::Result<()> {
    // Session lives for the whole process: weight load + EP/CUDA context
    // happen exactly once, on `load`. Warm `infer` calls pay IPC + compute.
    let mut session: Option<ort::session::Session> = None;
    let mut active_provider = String::from("none");

    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout();

    for line in stdin.lock().lines() {
        // A malformed line must never kill the serve-forever loop: answer
        // with a protocol error and keep serving (no id available to echo,
        // so the caller correlates by transport order).
        let req: serde_json::Value = match serde_json::from_str(&line?) {
            Ok(v) => v,
            Err(e) => {
                writeln!(
                    stdout,
                    "{}",
                    serde_json::json!({ "id": null, "status": "error",
                        "reason": "invalid-json", "detail": e.to_string() })
                )?;
                stdout.flush()?;
                continue;
            }
        };
        let id = req["id"].as_str().unwrap_or("").to_string();
        let op = req["op"].as_str().unwrap_or("").to_string();

        let resp = match op.as_str() {
            "health" => serde_json::json!({
                "id": id, "status": "alive",
                "modelLoaded": session.is_some(),
                "activeProvider": active_provider,
            }),
            "load" => {
                // The C# side sends a planner-approved plan (integrity-
                // qualified model path + ordered provider fallback already
                // authorized by IRuntimePlanner, hard-pin flag honored) —
                // the sidecar EXECUTES the plan, it never selects providers
                // or models itself.
                let _plan = req.clone();
                // Real code: ort::session::Session::builder()
                //   .with_execution_providers([...])?.commit_from_file(&model)?
                // Fallback chain tried in order; first that commits wins, and
                // ONLY a committed session may report "loaded" — the stub
                // below deliberately answers unimplemented so no placeholder
                // can ever claim a provider before commit succeeds.
                serde_json::json!({
                    "id": id, "status": "error",
                    "reason": "load-not-implemented-in-sketch",
                })
            }
            "infer" => {
                // Readiness keys ONLY on the loaded session: after any `load`
                // the provider name is set while the session is still a stub,
                // so gating on it would report "ready" with no model.
                if session.is_none() {
                    serde_json::json!({ "id": id, "status": "error",
                        "reason": "no-model-loaded" })
                } else {
                    // Real code: decode each { dtype, shape, data } envelope
                    // into ort::value::Value inputs, session.run(...), then
                    // base64 the PCM bytes into the same envelope shape.
                    let _inputs: HashMap<String, serde_json::Value> = serde_json::from_value(
                        req["inputs"].clone(),
                    )
                    .unwrap_or_default();
                    serde_json::json!({ "id": id, "status": "ok",
                        "outputs": {}, "inferMs": 0 })
                }
            }
            _ => serde_json::json!({ "id": id, "status": "error",
                "reason": "unknown-op" }),
        };

        writeln!(stdout, "{}", resp)?;
        stdout.flush()?;
    }
    Ok(())
}
