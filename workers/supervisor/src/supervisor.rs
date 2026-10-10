//! Process ownership and bounded, ordered JSON-lines exchanges.
use crate::protocol::{RawRequest, Response, PROTOCOL_VERSION};
use anyhow::Context;
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc::{self, Receiver, Sender};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};
const RESPONSE_TIMEOUT: Duration = Duration::from_secs(120);
const HEALTH_TIMEOUT: Duration = Duration::from_secs(30);
pub const CHATTERBOX_STAMP: &str = "trackdub-chatterbox-worker/0.1.0";
const BACKOFF_CAP: Duration = Duration::from_secs(60);
pub fn restart_backoff(attempt: u32) -> Duration {
    Duration::from_secs((1u64 << attempt.min(10)).min(BACKOFF_CAP.as_secs()))
}
type WriteJob = (String, Sender<anyhow::Result<()>>);
pub struct SupervisedWorker {
    child: Child,
    writes: Option<Sender<WriteJob>>,
    lines: Receiver<anyhow::Result<String>>,
    reader: Option<JoinHandle<()>>,
    writer: Option<JoinHandle<()>>,
    valid: bool,
}
impl SupervisedWorker {
    pub fn spawn(program: &str, args: &[&str]) -> anyhow::Result<Self> {
        let mut child = Command::new(program)
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .with_context(|| format!("spawning worker '{program}'"))?;
        let mut stdin = child.stdin.take().context("worker stdin unavailable")?;
        let stdout = child.stdout.take().context("worker stdout unavailable")?;
        let (tx, lines) = mpsc::channel();
        let reader = std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let done = line.is_err();
                if tx.send(line.context("reading worker stdout")).is_err() || done {
                    break;
                }
            }
        });
        let (writes, jobs) = mpsc::channel::<WriteJob>();
        let writer = std::thread::spawn(move || {
            while let Ok((line, ack)) = jobs.recv() {
                let result = writeln!(stdin, "{line}")
                    .and_then(|()| stdin.flush())
                    .context("writing worker stdin");
                let failed = result.is_err();
                if ack.send(result).is_err() || failed {
                    break;
                }
            }
        });
        Ok(Self {
            child,
            writes: Some(writes),
            lines,
            reader: Some(reader),
            writer: Some(writer),
            valid: true,
        })
    }
    pub fn request(&mut self, req: &RawRequest) -> anyhow::Result<Response> {
        self.request_with_timeout(req, RESPONSE_TIMEOUT)
    }
    /// Deadline includes writing blocked stdin and receiving the response.
    pub fn request_with_timeout(
        &mut self,
        req: &RawRequest,
        timeout: Duration,
    ) -> anyhow::Result<Response> {
        anyhow::ensure!(self.valid, "worker transport invalid; respawn required");
        Response::validate_inputs(&req.inputs)?;
        let line = serde_json::to_string(req).context("encoding request")?;
        let deadline = Instant::now() + timeout;
        let result = (|| {
            let (ack, written) = mpsc::channel();
            self.writes
                .as_ref()
                .context("worker transport closed")?
                .send((line, ack))
                .context("worker writer stopped")?;
            written
                .recv_timeout(deadline.saturating_duration_since(Instant::now()))
                .context("worker write timeout or writer stopped")??;
            let raw = self
                .lines
                .recv_timeout(deadline.saturating_duration_since(Instant::now()))
                .context("worker response timeout or reader stopped")??;
            let resp: Response = serde_json::from_str(&raw).context("parsing worker response")?;
            anyhow::ensure!(
                resp.protocol_version == PROTOCOL_VERSION,
                "worker protocol version mismatch"
            );
            anyhow::ensure!(resp.id == req.id, "worker response ID mismatch");
            Ok(resp)
        })();
        if result.is_err() {
            self.terminate();
        }
        result
    }
    fn terminate(&mut self) {
        self.valid = false;
        self.writes.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(writer) = self.writer.take() {
            let _ = writer.join();
        }
        if let Some(reader) = self.reader.take() {
            let _ = reader.join();
        }
    }
    pub fn respawn(&mut self, program: &str, args: &[&str]) -> anyhow::Result<()> {
        self.terminate();
        *self = Self::spawn(program, args)?;
        Ok(())
    }
    pub fn respawn_with_backoff(
        &mut self,
        program: &str,
        args: &[&str],
        attempt: u32,
    ) -> anyhow::Result<()> {
        std::thread::sleep(restart_backoff(attempt));
        self.respawn(program, args)
    }
    pub fn is_alive(&mut self) -> bool {
        self.valid && matches!(self.child.try_wait(), Ok(None))
    }
}
impl Drop for SupervisedWorker {
    fn drop(&mut self) {
        self.terminate();
    }
}
/// Health verifies process and protocol identity, never model readiness.
pub fn health_gate(program: &str, args: &[&str]) -> anyhow::Result<SupervisedWorker> {
    health_gate_with_stamp(program, args, CHATTERBOX_STAMP)
}
pub fn health_gate_with_stamp(
    program: &str,
    args: &[&str],
    expected_stamp: &str,
) -> anyhow::Result<SupervisedWorker> {
    let mut worker = SupervisedWorker::spawn(program, args)?;
    let req = RawRequest {
        id: Some("health-gate".into()),
        op: "health".into(),
        plan: None,
        inputs: Default::default(),
    };
    let resp = worker.request_with_timeout(&req, HEALTH_TIMEOUT)?;
    anyhow::ensure!(
        resp.status == "alive",
        "worker failed health gate: {} {:?}",
        resp.status,
        resp.reason
    );
    anyhow::ensure!(
        resp.extra.get("worker").and_then(serde_json::Value::as_str) == Some(expected_stamp),
        "worker release stamp missing or mismatched"
    );
    anyhow::ensure!(
        worker.is_alive(),
        "worker died immediately after health gate"
    );
    Ok(worker)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn backoff_grows_then_caps() {
        assert_eq!(restart_backoff(0), Duration::from_secs(1));
        assert_eq!(restart_backoff(2), Duration::from_secs(4));
        assert_eq!(restart_backoff(100), BACKOFF_CAP);
    }
}
