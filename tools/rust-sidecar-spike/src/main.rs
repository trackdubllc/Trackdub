// SKETCH — load-once / serve-forever inference sidecar.
// Not compiled in CI. Illustrates the seam; details TBD in the spike.
//
// Protocol: JSON-lines on stdin/stdout.
//   {"id":"..","op":"load","model":"..","providers":[...]} -> {"id":"..","status":"loaded",...}
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
        let req: serde_json::Value = serde_json::from_str(&line?)?;
        let id = req["id"].as_str().unwrap_or("").to_string();
        let op = req["op"].as_str().unwrap_or("").to_string();

        let resp = match op.as_str() {
            "health" => serde_json::json!({
                "id": id, "status": "alive",
                "modelLoaded": session.is_some(),
                "activeProvider": active_provider,
            }),
            "load" => {
                let model = req["model"].as_str().unwrap_or("").to_string();
                // Provider preference order comes from C#; the sidecar
                // reports back what it ACTUALLY settled on (honest readiness).
                let providers: Vec<String> = req["providers"]
                    .as_array()
                    .map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect())
                    .unwrap_or_else(|| vec!["CPU".into()]);

                // Real code: ort::session::Session::builder()
                //   .with_execution_providers([...])?.commit_from_file(&model)?
                // Fallback chain tried in order; first that commits wins.
                active_provider = providers.into_iter().next().unwrap_or("CPU".into());
                session = None; // placeholder: real session goes here
                let _ = &model;
                serde_json::json!({
                    "id": id, "status": "loaded",
                    "activeProvider": active_provider, "loadMs": 0,
                })
            }
            "infer" => {
                if session.is_none() && active_provider == "none" {
                    serde_json::json!({ "id": id, "status": "error",
                        "reason": "no-model-loaded" })
                } else {
                    // Real code: build ort::value::Value inputs from
                    // req["inputs"], session.run(...), base64 the PCM bytes.
                    let _inputs: HashMap<String, serde_json::Value> = serde_json::from_value(
                        req["inputs"].clone().unwrap_or_default(),
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
