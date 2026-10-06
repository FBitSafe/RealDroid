# Changelog

## Fix017
- CP-step assist: `CpStepFadeGenerations` default `100 → 0` (no fade); `CapturePointAssistForGeneration` returns `1f` when `<= 0`; startup log says `100% (no fade)`.
- Swing-leg reflex protection: `DoNotTouchSwingingLeg` default `false → true`; `MakeRun` sets `!_allowSwingLegReflex` unconditionally (was gated on `_protocolTest`).
- Pain levels remap: impact-into-stop → Ache (not Acute); hose-tear → Acute (not Ache); broken-joint-over-limit adds Ache+Acute proportional to how far past the limit and how fast moving. New fields: `ImpactAche`, `OverAche`, `OverAcute`, `OverVel`.
- New arg `--hip-upper X` (0.3–1.2): overrides `hip_n`/`hip_f` Upper in RagdollDef; only valid with `--motor-rom-step-test`; printed in `ROMSTEP` line as `hipUpper=`.
- Chip backup: on dream start (not probe/test/fresh, file exists) copies chip to `<chipdir>/backup/<name>_g<gen>_<timestamp>.chip`.
- Dream log mirror: each log line also written to `res://Diagnostics/DreamLogs/<same filename>` when not running from exported template. Mirror errors are silent.
- `ARCHITECTURE.md`: extended "Сборка и проверка" with Fix017 commands.
- `CHANGELOG.md`: created.

## Fix016
- Per-dream log files in `user://logs/dream_<timestamp>_<sector>.txt` via `DreamLog.Begin()`.
- Refactor: all root-level `.cs` files moved into subdirectories (`Core/`, `Body/`, `Chips/`, `Dream/`, `UI/`); missing `using` directives restored.
