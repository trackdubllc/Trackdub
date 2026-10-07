//! trackdub-supervisor: health-gate (and later, supervise) inference workers.
//!
//! Usage: `trackdub-supervisor --check <program> [args...]`
//! `--check` is an explicit one-shot probe: it spawns the worker, runs the
//! protocol health gate, prints the readiness verdict as JSON, then reaps the
//! child (via `SupervisedWorker`'s `Drop`) and exits 0 on `alive` / nonzero
//! otherwise. No resident worker is handed off here — keeping a worker alive
//! across requests is the C# host's job (a follow-up integration).

use trackdub_supervisor::{protocol, supervisor};

fn main() -> anyhow::Result<()> {
    let argv: Vec<String> = std::env::args().collect();
    anyhow::ensure!(
        argv.len() >= 3 && argv[1] == "--check",
        "usage: trackdub-supervisor --check <program> [args...]"
    );
    let args: Vec<&str> = argv[3..].iter().map(String::as_str).collect();

    let worker = supervisor::health_gate(&argv[2], &args)?;
    let verdict = serde_json::json!({
        "supervisor": protocol::SUPERVISOR_STAMP,
        "protocolVersion": protocol::PROTOCOL_VERSION,
        "status": "accepted",
    });
    println!("{}", serde_json::to_string(&verdict)?);

    // One-shot probe: the worker drops here, killing and reaping the child we
    // just gate-checked. Nothing is leaked or left orphaned.
    drop(worker);
    Ok(())
}
