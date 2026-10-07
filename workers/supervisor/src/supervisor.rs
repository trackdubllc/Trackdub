//! Process supervision: spawn a worker, speak the protocol strictly
//! request-response (no interleaving), enforce the version stamp, restart with
//! capped exponential backoff. The supervisor owns everything users hate about
//! runtimes; the worker only runs models.

use crate::protocol::{RawRequest, Response, Status, PROTOCOL_VERSION};
use anyhow::Context;
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::Duration;

/// How long to wait for one worker response before declaring it wedged.
const RESPONSE_TIMEOUT: Duration = Duration::from_secs(120);
/// Backoff base and cap for crash restarts.
const BACKOFF_BASE: Duration = Duration::from_secs(1);
const BACKOFF_CAP: Duration = Duration::from_secs(60);

/// Capped exponential backoff for crash restarts: 1s, 2s, 4s, … capped at 60s.
/// Attempt counting starts at 0 (first restart waits the base).
pub fn restart_backoff(attempt: u32) -> Duration {
    let shift = attempt.min(10);
    let secs = BACKOFF_BASE.as_secs().saturating_mul(1 << shift);
    Duration::from_secs(secs.min(BACKOFF_CAP.as_secs()))
}

/// A live worker child with a dedicated stdout reader thread. Requests are
/// strictly ordered: one in flight at a time, matched by response id. A
/// timeout or out-of-order response poisons the connection (kills the child);
/// the caller must respawn before the next request.
pub struct SupervisedWorker {
    child: Child,
    stdin: ChildStdin,
    lines: Receiver<anyhow::Result<String>>,
    _reader: std::thread::JoinHandle<()>,
    poisoned: bool,
}

impl Drop for SupervisedWorker {
    fn drop(&mut self) {
        // std::process::Child does NOT kill on drop; a wedged or nonconforming
        // worker would otherwise be orphaned. Kill best-effort and reap.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

impl SupervisedWorker {
    /// Spawn `program args` with piped stdio and start the reader thread.
    pub fn spawn(program: &str, args: &[&str]) -> anyhow::Result<Self> {
        let mut child = Command::new(program)
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .with_context(|| format!("spawning worker '{program}'"))?;
        let stdin = child.stdin.take().context("worker stdin unavailable")?;
        let stdout = child.stdout.take().context("worker stdout unavailable")?;
        let (tx, rx): (Sender<anyhow::Result<String>>, _) = mpsc::channel();
        let reader = std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let done = line.is_err();
                if tx.send(line.context("reading worker stdout")).is_err() || done {
                    break;
                }
            }
        });
        Ok(Self {
            child,
            stdin,
            lines: rx,
            _reader: reader,
            poisoned: false,
        })
    }

    /// Send one request, return the next response line parsed. Refuses to
    /// serve a worker whose protocol version mismatches, and poisons the
    /// connection when a timeout or response-id mismatch means the stream can
    /// no longer be trusted to answer the request that was just sent.
    pub fn request(&mut self, req: &RawRequest) -> anyhow::Result<Response> {
        anyhow::ensure!(
            !self.poisoned,
            "worker connection is unusable after a timeout or out-of-order response; respawn first"
        );
        // Boundary-validate inbound tensor envelopes before they touch the wire.
        Response::validate_inputs(&req.inputs)
            .with_context(|| "rejecting malformed request inputs")?;
        let line = serde_json::to_string(req).context("encoding request")?;
        writeln!(self.stdin, "{line}").context("writing worker stdin")?;
        self.stdin.flush().context("flushing worker stdin")?;
        let raw = match self.lines.recv_timeout(RESPONSE_TIMEOUT) {
            Ok(Ok(line)) => line,
            Ok(Err(e)) => {
                self.poison();
                return Err(e.context("reading worker stdout"));
            }
            Err(_) => {
                self.poison();
                anyhow::bail!("worker response timeout — wedged or dead");
            }
        };
        let resp: Response = serde_json::from_str(&raw).context("parsing worker response")?;
        if resp.protocol_version != PROTOCOL_VERSION {
            self.poison();
            anyhow::bail!(
                "worker protocol version {} != supervisor {} — refusing to serve",
                resp.protocol_version,
                PROTOCOL_VERSION
            );
        }
        if let Some(req_id) = &req.id {
            let matches = match &resp.id {
                Some(resp_id) => resp_id == req_id,
                None => false,
            };
            anyhow::ensure!(
                matches,
                "worker answered id {:?} to request {:?}; stream out of sync, refusing to serve",
                resp.id,
                req.id
            );
        }
        Ok(resp)
    }

    /// Mark the connection unusable: a timed-out or mismatched response means
    /// a late line could still be queued and be misattributed to a later
    /// request. Kill the child so no further request can consume it.
    fn poison(&mut self) {
        self.poisoned = true;
        let _ = self.child.kill();
    }

    /// Kill the child (best-effort) and spawn a replacement for the same target.
    pub fn respawn(&mut self, program: &str, args: &[&str]) -> anyhow::Result<()> {
        let _ = self.child.kill();
        let _ = self.child.wait();
        *self = Self::spawn(program, args)?;
        Ok(())
    }

    /// Respawn after `restart_backoff(attempt)`. Sleeps first so crash loops
    /// cannot spin; the caller owns attempt counting per worker lifetime.
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
        !self.poisoned && matches!(self.child.try_wait(), Ok(None))
    }
}

/// Health-gate a worker target: spawn, ask health, require `alive` with a
/// model-loadable contract. Returns the worker on success for the caller to
/// keep supervising; drops (killing + reaping the child) on any failure.
pub fn health_gate(program: &str, args: &[&str]) -> anyhow::Result<SupervisedWorker> {
    let mut worker = SupervisedWorker::spawn(program, args)?;
    let req = RawRequest {
        id: Some("health-gate".to_string()),
        op: "health".to_string(),
        plan: None,
        inputs: Default::default(),
    };
    let resp = worker.request(&req)?;
    anyhow::ensure!(
        resp.status == Status::Alive,
        "worker failed health gate: status={:?} reason={:?}",
        resp.status,
        resp.reason
    );
    anyhow::ensure!(worker.is_alive(), "worker died immediately after health gate");
    Ok(worker)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backoff_grows_then_caps() {
        assert_eq!(restart_backoff(0), Duration::from_secs(1));
        assert_eq!(restart_backoff(1), Duration::from_secs(2));
        assert_eq!(restart_backoff(2), Duration::from_secs(4));
        assert_eq!(restart_backoff(100), BACKOFF_CAP);
    }
}
