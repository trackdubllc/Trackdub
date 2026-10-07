//! trackdub-supervisor: health-gate (and later, supervise) inference workers.
//!
//! Usage: `trackdub-supervisor --check <program> [args...]`
//! Spawns the worker, runs the protocol health gate, prints the readiness
//! verdict as JSON, and exits 0 on `alive` / nonzero otherwise. A successful
//! gate hands a live, resident worker to the caller, so main deliberately
//! does not kill it on the success path.

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

    // A successful gate hands a live, warmed worker to the caller (later: the
    // C# host). Dropping SupervisedWorker here would kill the child on scope
    // exit; forgetting keeps it resident, which is the entire point.
    std::mem::forget(worker);
    Ok(())
}
