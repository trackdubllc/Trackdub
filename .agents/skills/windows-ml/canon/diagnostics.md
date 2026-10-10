# Microsoft-documented diagnostics

Tier 1: Microsoft Learn only. Checked 2026-10-10.

## Download or registration failures

Microsoft identifies pending-reboot updates, paused Windows Update and managed-device policy as common download blockers. A download error is not evidence of model incompatibility. For unresolved issues, use Feedback Hub's Developer Platform > Windows Machine Learning category. [Troubleshooting](https://learn.microsoft.com/windows/ai/new-windows-ml/execution-provider-errors).

## Separate lifecycle observations

Use `FindAllProviders()`/`ReadyState` for catalog offering and preparation; readiness result status for preparation/install outcome; registration results/`GetEpDevices()` for ORT visibility; explicit selection for session configuration. [Install](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers), [register](https://learn.microsoft.com/windows/ai/new-windows-ml/register-execution-providers), [select](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers).

## ETW and rundown logs

Microsoft's procedure uses `WinML.wprp`, `WindowsMLProfile.wpaProfile` and `Get-WinMLRundown.ps1`. WPR operations require administrator privileges and Windows Performance Toolkit. The official guide provides download locations and commands. Keep capture sessions short. [Capture logs](https://learn.microsoft.com/windows/ai/new-windows-ml/logs).

Rundown logs group data by process and include ORT/Windows ML versions, hardware/drivers, model/session information, EP selection, packages and errors. Use those categories to investigate deployment, placement and loading rather than assuming a preferred device policy selected the intended hardware. [Interpretation](https://learn.microsoft.com/windows/ai/new-windows-ml/logs).

## Cache failures and changed behavior

Check compatibility status, selected device group and source identity. Recheck runtime/EP/driver versions after updates. `EP_NOT_APPLICABLE` does not validate a cache. [Compilation](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation), [updates](https://learn.microsoft.com/windows/ai/new-windows-ml/update-execution-providers).

## Evidence boundary

Distinguish documentation findings, sample compilation, discovery, session creation and actual inference. Do not claim a model/stage passed from documentation alone. If Learn does not explain an error, state that the cause is unresolved under the permitted source boundary.
