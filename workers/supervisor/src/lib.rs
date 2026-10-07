//! trackdub-supervisor library: sidecar wire protocol plus process supervision.
//! The binary (`src/main.rs`) is a thin health-gate entry point; the C# host
//! and future supervise-forever loop build on this API.

pub mod protocol;
pub mod supervisor;
