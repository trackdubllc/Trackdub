//! Sidecar wire protocol: JSON-lines on stdin/stdout, typed tensor envelopes,
//! planner-approved load plans. Shared verbatim with the Python worker and the
//! C# host. See `workers/PROTOCOL.md`.

use anyhow::Context;
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
    /// e.g. "int64", "float32", "int16", "utf8".
    pub dtype: String,
    pub shape: Vec<i64>,
    pub data: String,
}

impl TensorEnvelope {
    /// Reject malformed envelopes at the boundary: empty dtype, negative dims,
    /// payload that is not strictly valid base64, or a payload whose decoded
    /// byte count does not match `shape` x `dtype` item size.
    pub fn validate(&self) -> anyhow::Result<()> {
        anyhow::ensure!(!self.dtype.is_empty(), "tensor dtype must not be empty");
        anyhow::ensure!(
            self.shape.iter().all(|&d| d >= 0),
            "tensor shape must not contain negative dims"
        );
        let decoded = base64_decode_len(&self.data)?;
        if let Some(item_size) = dtype_item_size(&self.dtype) {
            let elements = self
                .shape
                .iter()
                .try_fold(1i64, |acc, &d| acc.checked_mul(d))
                .unwrap_or(i64::MAX);
            let expected = (elements as u128).saturating_mul(item_size as u128);
            anyhow::ensure!(
                (decoded as u128) == expected,
                "payload decodes to {decoded} bytes but {}/{} shape {:?} requires {expected}",
                self.dtype,
                item_size,
                self.shape
            );
        }
        Ok(())
    }
}

/// Item size in bytes for the dtypes the protocol defines. `utf8` is the text
/// envelope: one byte per element, so `shape` is the encoded byte count.
/// Unknown dtypes are tolerated but not size-checked (forward compatibility).
fn dtype_item_size(dtype: &str) -> Option<u32> {
    match dtype {
        "int8" | "uint8" | "utf8" => Some(1),
        "int16" | "uint16" | "float16" => Some(2),
        "int32" | "uint32" | "float32" => Some(4),
        "int64" | "uint64" | "float64" => Some(8),
        _ => None,
    }
}

/// Strict base64 validation: alphabet and padding checks, returning the
/// decoded byte length. Rejects non-alphabet characters, impossible lengths
/// (`len % 4 == 1`), and illegal padding counts — a payload like `"!!!!"`
/// cannot slip through on a length check alone.
fn base64_decode_len(data: &str) -> anyhow::Result<usize> {
    let bytes = data.as_bytes();
    let total = bytes.len();
    if total == 0 {
        return Ok(0);
    }
    let pad = bytes.iter().rev().take_while(|&&b| b == b'=').count();
    anyhow::ensure!(pad <= 2, "payload has more than 2 padding chars");
    let unpadded = total - pad;
    if pad > 0 {
        anyhow::ensure!(total.is_multiple_of(4), "padded payload length {total} is not a multiple of 4");
        let needed = match unpadded % 4 {
            3 => 1,
            2 => 2,
            _ => 0,
        };
        anyhow::ensure!(needed != 0 && pad == needed,
            "payload has {pad} padding chars but {needed} are required");
    } else {
        anyhow::ensure!(total % 4 != 1, "payload length {total} is never valid base64");
    }
    anyhow::ensure!(
        bytes[..unpadded].iter().all(|&b| {
            b.is_ascii_alphanumeric() || b == b'+' || b == b'/'
        }),
        "payload contains non-base64 characters"
    );
    Ok((unpadded / 4) * 3
        + match unpadded % 4 {
            2 => 1,
            3 => 2,
            _ => 0,
        })
}

/// Planner-approved load plan. The sidecar executes it; it never selects
/// models or providers itself (mirrors `StageRuntimePlan`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LoadPlan {
    /// Integrity-qualified model path or HF repo id.
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

/// Closed status vocabulary (PROTOCOL.md). Deserializing a response with an
/// unsupported status fails at the boundary instead of passing a value the
/// supervisor does not understand.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Status {
    Alive,
    Loaded,
    Ok,
    Error,
}

/// Closed `reason` vocabulary (PROTOCOL.md). Unknown reasons fail at the
/// boundary instead of being passed through as diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Reason {
    InvalidJson,
    UnknownOp,
    BadPlan,
    BadInputs,
    DependencyMissing,
    ModelNotFound,
    LoadFailed,
    NoModelLoaded,
    InferFailed,
    LoadNotImplemented,
}

/// One outbound response line.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Response {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    /// `alive` | `loaded` | `ok` | `error`.
    pub status: Status,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<Reason>,
    #[serde(flatten)]
    pub extra: HashMap<String, serde_json::Value>,
    #[serde(rename = "protocolVersion")]
    pub protocol_version: u32,
}

impl Response {
    fn base(id: Option<String>, status: Status) -> Self {
        Self {
            id,
            status,
            reason: None,
            extra: HashMap::new(),
            protocol_version: PROTOCOL_VERSION,
        }
    }

    pub fn error(id: Option<String>, reason: Reason) -> Self {
        let mut r = Self::base(id, Status::Error);
        r.reason = Some(reason);
        r
    }

    /// The malformed-line answer every worker must give (and the supervisor
    /// must accept): no id available, protocol version always present.
    pub fn invalid_json(detail: &str) -> Self {
        Self::error(None, Reason::InvalidJson).with(
            "detail",
            serde_json::Value::String(detail.to_string()),
        )
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
    fn envelope_rejects_non_base64_alphabet() {
        let env = TensorEnvelope {
            dtype: "int16".to_string(),
            shape: vec![2],
            data: "!!!!".to_string(), // length 4 is fine, characters are not
        };
        assert!(env.validate().is_err());
    }

    #[test]
    fn envelope_rejects_illegal_padding() {
        let env = TensorEnvelope {
            dtype: "int16".to_string(),
            shape: vec![2],
            data: "AAA=".to_string(), // unpadded part % 4 == 0 needs no padding
        };
        assert!(env.validate().is_err());
    }

    #[test]
    fn envelope_rejects_decoded_size_mismatch() {
        // int64 shape [1, 3] needs 24 bytes; this payload decodes to 8.
        let env = TensorEnvelope {
            dtype: "int64".to_string(),
            shape: vec![1, 3],
            data: "AAAAAAAAAAA=".to_string(),
        };
        assert!(env.validate().is_err());
    }

    #[test]
    fn envelope_accepts_well_formed() {
        let env = TensorEnvelope {
            dtype: "int64".to_string(),
            shape: vec![1, 3],
            data: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA".to_string(), // 24 zero bytes
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
        let r = Response::error(None, Reason::InvalidJson);
        let v = serde_json::to_value(&r).expect("serializes");
        assert_eq!(v["protocolVersion"], 1);
        assert_eq!(v["status"], "error");
    }

    #[test]
    fn invalid_json_answer_has_no_id_but_names_the_reason() {
        let v = serde_json::to_value(Response::invalid_json("expected value")).expect("serializes");
        assert!(v.get("id").is_none());
        assert_eq!(v["reason"], "invalid-json");
        assert!(v["detail"].as_str().unwrap_or_default().contains("expected value"));
    }

    #[test]
    fn validate_inputs_names_the_bad_tensor() {
        let mut inputs = HashMap::new();
        inputs.insert(
            "input_ids".to_string(),
            TensorEnvelope {
                dtype: "int64".to_string(),
                shape: vec![1, 3],
                data: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA".to_string(),
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

    #[test]
    fn response_status_is_a_closed_vocabulary() {
        let ok: Response =
            serde_json::from_str(r#"{"status":"alive","protocolVersion":1}"#).expect("alive parses");
        assert_eq!(ok.status, Status::Alive);
        let bogus: Result<Response, _> =
            serde_json::from_str(r#"{"status":"flying","protocolVersion":1}"#);
        assert!(bogus.is_err(), "unsupported status must fail at the boundary");
    }
}
