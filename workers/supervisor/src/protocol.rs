//! Sidecar wire protocol: JSON-lines on stdin/stdout, typed tensor envelopes,
//! planner-approved load plans. Shared verbatim with the Python worker and the
//! C# host. See `workers/PROTOCOL.md`.

use anyhow::Context;
use base64::{engine::general_purpose::STANDARD, Engine};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Protocol version the supervisor speaks. Every worker response MUST carry
/// this under `protocolVersion`; any other value refuses to serve.
pub const PROTOCOL_VERSION: u32 = 1;

/// Release stamp of this supervisor build. The C# host refuses a worker whose
/// stamp it does not recognize (same fingerprint discipline as engine caches).
pub const SUPERVISOR_STAMP: &str = "trackdub-supervisor/0.1.0";

/// Typed tensor envelope on the wire. Payload is base64; shape is row-major.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TensorEnvelope {
    /// e.g. "int64", "float32", "int16".
    pub dtype: String,
    pub shape: Vec<i64>,
    pub data: String,
}

impl TensorEnvelope {
    /// Reject malformed envelopes at the boundary: empty dtype, negative dims,
    /// or payload that is not valid base64.
    pub fn validate(&self) -> anyhow::Result<()> {
        anyhow::ensure!(!self.dtype.is_empty(), "tensor dtype must not be empty");
        anyhow::ensure!(
            self.shape.iter().all(|&d| d >= 0),
            "tensor shape must not contain negative dims"
        );
        let bytes = STANDARD
            .decode(&self.data)
            .context("invalid base64 payload")?;
        if self.dtype == "utf8" {
            anyhow::ensure!(
                self.shape == vec![bytes.len() as i64],
                "utf8 shape must equal byte count"
            );
            std::str::from_utf8(&bytes).context("invalid utf8 payload")?;
        }
        Ok(())
    }
}

/// Planner-approved load plan. The sidecar executes it; it never selects
/// models or providers itself (mirrors `StageRuntimePlan`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LoadPlan {
    /// Planner-approved local model directory. Repository IDs and arbitrary
    /// weight files are not accepted by the Chatterbox worker.
    pub model: String,
    /// Ordered provider fallback, already authorized by `IRuntimePlanner`.
    #[serde(default)]
    pub providers: Vec<String>,
    /// Honor `RequirePreferredExecutionProvider`: no silent fallback.
    #[serde(default, rename = "requirePreferred")]
    pub require_preferred: bool,
    /// Optional reference-voice audio path for cloning. The worker fails the
    /// load loudly when it is set but unreadable — never silently falls back
    /// to the default voice.
    #[serde(default, rename = "voicePromptPath")]
    pub voice_prompt_path: Option<String>,
}

/// One inbound request line.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RawRequest {
    pub id: Option<String>,
    /// "health" | "load" | "infer".
    pub op: String,
    #[serde(default)]
    pub plan: Option<LoadPlan>,
    #[serde(default)]
    pub inputs: HashMap<String, TensorEnvelope>,
}

/// One outbound response line.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Response {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    /// "alive" | "loaded" | "ok" | "error".
    pub status: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    #[serde(flatten)]
    pub extra: HashMap<String, serde_json::Value>,
    #[serde(rename = "protocolVersion")]
    pub protocol_version: u32,
}

impl Response {
    fn base(id: Option<String>, status: &str) -> Self {
        Self {
            id,
            status: status.to_string(),
            reason: None,
            extra: HashMap::new(),
            protocol_version: PROTOCOL_VERSION,
        }
    }

    pub fn error(id: Option<String>, reason: &str) -> Self {
        let mut r = Self::base(id, "error");
        r.reason = Some(reason.to_string());
        r
    }

    /// The malformed-line answer every worker must give (and the supervisor
    /// must accept): no id available, protocol version always present.
    pub fn invalid_json(detail: &str) -> Self {
        Self::error(None, "invalid-json")
            .with("detail", serde_json::Value::String(detail.to_string()))
    }

    /// Boundary-validate every inbound tensor envelope before dispatch.
    pub fn validate_inputs(inputs: &HashMap<String, TensorEnvelope>) -> anyhow::Result<()> {
        for (name, env) in inputs {
            env.validate()
                .with_context(|| format!("input tensor '{name}' is malformed"))?;
        }
        Ok(())
    }

    pub fn with(mut self, key: &str, value: serde_json::Value) -> Self {
        self.extra.insert(key.to_string(), value);
        self
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn envelope_rejects_empty_dtype() {
        let env = TensorEnvelope {
            dtype: String::new(),
            shape: vec![1, 3],
            data: "AAAA".to_string(),
        };
        assert!(env.validate().is_err());
    }

    #[test]
    fn envelope_rejects_negative_dims() {
        let env = TensorEnvelope {
            dtype: "int64".to_string(),
            shape: vec![1, -1],
            data: "AAAA".to_string(),
        };
        assert!(env.validate().is_err());
    }

    #[test]
    fn envelope_rejects_impossible_base64_length() {
        let env = TensorEnvelope {
            dtype: "int16".to_string(),
            shape: vec![5],
            data: "ABCDE".to_string(), // len % 4 == 1
        };
        assert!(env.validate().is_err());
    }

    #[test]
    fn envelope_accepts_well_formed() {
        let env = TensorEnvelope {
            dtype: "int64".to_string(),
            shape: vec![1, 3],
            data: "AAAAAAAAAAA=".to_string(),
        };
        assert!(env.validate().is_ok());
    }

    #[test]
    fn load_plan_parses_camel_case_pin_flag() {
        let req: RawRequest = serde_json::from_str(
            r#"{"id":"req-1","op":"load","plan":{"model":"m","providers":["CPU"],"requirePreferred":true}}"#,
        )
        .expect("plan parses");
        let plan = req.plan.expect("plan present");
        assert!(plan.require_preferred);
        assert_eq!(plan.providers, vec!["CPU".to_string()]);
        assert_eq!(plan.voice_prompt_path, None);
    }

    #[test]
    fn load_plan_carries_voice_prompt_path() {
        let req: RawRequest = serde_json::from_str(
            r#"{"id":"req-2","op":"load","plan":{"model":"m","voicePromptPath":"/v/ref.wav"}}"#,
        )
        .expect("plan parses");
        let plan = req.plan.expect("plan present");
        assert_eq!(plan.voice_prompt_path.as_deref(), Some("/v/ref.wav"));
    }

    #[test]
    fn error_response_always_carries_protocol_version() {
        let r = Response::error(None, "invalid-json");
        let v = serde_json::to_value(&r).expect("serializes");
        assert_eq!(v["protocolVersion"], 1);
        assert_eq!(v["status"], "error");
    }

    #[test]
    fn invalid_json_answer_has_no_id_but_names_the_reason() {
        let v =
            serde_json::to_value(&Response::invalid_json("expected value")).expect("serializes");
        assert!(v.get("id").is_none());
        assert_eq!(v["reason"], "invalid-json");
        assert!(v["detail"]
            .as_str()
            .unwrap_or_default()
            .contains("expected value"));
    }

    #[test]
    fn validate_inputs_names_the_bad_tensor() {
        let mut inputs = HashMap::new();
        inputs.insert(
            "input_ids".to_string(),
            TensorEnvelope {
                dtype: "int64".to_string(),
                shape: vec![1, 3],
                data: "AAAAAAAAAAA=".to_string(),
            },
        );
        inputs.insert(
            "bad".to_string(),
            TensorEnvelope {
                dtype: String::new(),
                shape: vec![],
                data: String::new(),
            },
        );
        let err = Response::validate_inputs(&inputs).expect_err("bad tensor must fail");
        assert!(err.to_string().contains("'bad'"), "unexpected: {err}");
    }
}
