# Changelog

## Fix020
- RECOVER v1 = голеностоп+бёдра без шага: CP-шаг выключен по умолчанию, вернётся в WALK; `--cp-step` для экспериментов.
- `MotorRom.CapturePointStepping` и `NeuralMotorChip.BaseStepping` по умолчанию `false`; код шага не удалён.
- Dream: новый аргумент `--cp-step` (включает шаг, в `gen`/стартовом логе `CP step 100 %` / `100% (no fade)`); при выключенном шаге стартовый лог `OFF (RECOVER v1)`, в `gen` пишется `CP step off`. `--no-cp-step` оставлен для совместимости.
- Откат чипа: ночной результат сохранён как `motor_nn_cpstep_night.chip`; рабочий `motor_nn.chip` восстановлен из резерва перед ночным сном (`motor_nn_g0136_2026-10-07T00-01-31.chip`, gen 136).
- Толчок в игре в процентах: общий `PushModel.Impulse(level, dir, body)`; `Main.PushLevel = 0.45`, `[`/`]` — ±0.05 в 0.05..1.0; `Q` = в грудь, `E` = в спину; HUD-строка на BrainPanel.

## Fix019
- Gen timing: wall-clock seconds per generation appended to each `gen` log line as `| Xs/gen ETA hh:mm` (ETA = rolling avg of last 5 gens × remaining gens, only when `MaxGenerations > 0`).
- `dream END` line added on completion: `dream END gens=N total=hh:mm:ss best exam=X.XXX (gen K)`.
- `tools/dream_recover.bat`: cmd batch, 200 gens RECOVER by default, args `[gens] [chip] [nogit]`, auto git commit+push of DreamLogs after run.
- Trial run confirmed: `100% (no fade)`, `CP step 100%`, `~5s/gen`, `dream END` present.

## Fix018
- `tools/godot.ps1` created. Bug found: PowerShell strips bare `--`; workaround is quoting as `'--'`. All ARCHITECTURE.md commands updated.
- Fix017 partial-application bug corrected: `CpStepFadeGenerations` was still 100, `CapturePointAssistForGeneration` still returned `0f`, startup log block misindented. All fixed.
- All Fix017 §6 checks run and passed. Results in Diagnostics013.txt.

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
