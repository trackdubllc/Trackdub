# GenAI native-pair construction probe

Run Invoke-Probe.ps1 with -ModelDirectory pointing to an existing GenAI model directory.
The script builds a Windows-TFM probe and constructs a CPU Config/Model in a fresh child
process. A native crash, managed failure or timeout fails the parent script without loading
GenAI into the parent. No model is downloaded. A successful construction is not a generation
or accelerator validation. The probe prints selected paths, package flavor and versions.

The current explicit support policy covers Windows ML 2.4.89 / native ORT 1.27.1 /
GenAI.WinML 0.17.1, and stock ORT 1.30.0 / GenAI CPU or CUDA 0.17.1.
Upgrades require updating and validating the policy. DNNL and unknown native deployments
are not verified; a Windows GenAI request fails before Config or Model construction.
Build-generated hashes are provenance checks, not a security boundary against someone
who can replace both the managed assembly and native binaries.

Unit tests use synthetic files to test provenance and selection policy; only this child
probe exercises real native model construction. Run it separately after targeted unit tests.

To opt into the integration test, set TRACKDUB_GENAI_PROBE_EXE to the built
Trackdub.GenAiProbe.exe and TRACKDUB_GENAI_PROBE_MODEL to the model directory, then run
GenAiNativeConstructionProbeTests on the Windows TFM. Without those fixtures it is
explicitly skipped, not passed. The integration test has a 120-second child timeout.
