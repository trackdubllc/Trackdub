//! Transient health probe: child is terminated before this command exits.
use trackdub_supervisor::{protocol, supervisor};
fn main() -> anyhow::Result<()> {
    let argv: Vec<String> = std::env::args().collect();
    anyhow::ensure!(
        argv.len() >= 3 && argv[1] == "--check",
        "usage: trackdub-supervisor --check <program> [args...]"
    );
    let args: Vec<&str> = argv[3..].iter().map(String::as_str).collect();
    let worker = supervisor::health_gate(&argv[2], &args)?;
    drop(worker);
    println!(
        "{}",
        serde_json::json!({"supervisor": protocol::SUPERVISOR_STAMP, "protocolVersion": protocol::PROTOCOL_VERSION, "status": "accepted", "probe": "transient"})
    );
    Ok(())
}
