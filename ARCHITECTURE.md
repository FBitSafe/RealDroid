# Структура проекта

Godot 4.7 .NET (C#), физика 240 Гц. Пространств имён нет: все типы глобальные, поэтому
перенос файла между папками ничего не ломает. Папки только группируют код.

## Папки

| Папка | Что внутри |
|---|---|
| `Core/` | Мозг и его контракты: `Brain`, `Slot`, `Bus`, `IChip`, `LobeKind`, `Protocol`, `Bal`, `Rng`. Не зависит от тела и сна. |
| `Chips/` | Всё, что вставляется в слоты мозга: ROM-чипы (`FirmwareRom`, `VestibularRom`, `ReflexRom`, `ArbiterRom`, `MotorRom`), нейронный `NeuralMotorChip`, файл чипа `ChipFile`, арбитр-заглушка `LockArbiter`. |
| `Body/` | Тело: `Ragdoll`, `PartBody`, описание `RagdollDef`/`PartDef`/`JointDef`/`LimbSide`. |
| `Body/Damage/` | Износ и боль: `Durability`, `DamageMode`, `DamageSnapshot`, `Nociception`, `DamageFx`. |
| `Dream/` | Сон (обучение): `Dream` (три partial-файла), `DreamJob`, `DreamLog`, `EsOptimizer`. |
| `Dream/Tasks/` | Контракты учебных программ: `IDreamTask`, `IEpisode`, `ICurriculum`, `IExamVerdict`, `DreamScoreTerm`, фабрика `DreamTasks`. |
| `Dream/Tasks/Stand/`, `Dream/Tasks/Recover/` | Конкретные задачи: `*Task` (параметры) и `*Episode` (один прогон). |
| `UI/` | `BrainPanel`, `DebugOverlay`, `DreamScoreGraph`. |
| корень | `Main.cs` + `Main.tscn` (игра), `Dream.tscn` (сцена сна), `project.godot`. Сцены остаются в корне: пути `res://Main.tscn` и `res://Dream.tscn` используются в командах запуска. |
| `Context/`, `Fixes/`, `Diagnostics/` | Передача контекста, задания агенту и его отчёты. Код не содержат. |

## Как части связаны

```
Main (игра) ──► Ragdoll ──► Brain ──► Slot[5] ──► IChip
                  │            │
                  │            └─ Bus: именованные float[]-каналы
                  └─ Durability / Nociception / DamageFx

Main ──(DreamJob: чип + DamageSnapshot)──► Dream.tscn
Dream: EsOptimizer ─► NeuralMotorChip(W) ─► N клонов Ragdoll ─► IDreamTask.Begin() ─► IEpisode.Step()
Dream ──► ChipFile.Save (лучший чип) ──► DreamJob.Waking ──► Main.tscn
```

**RECOVER v1 = голеностоп+бёдра без шага; CP-шаг выключен, вернётся в WALK; `--cp-step` для экспериментов.**

## Правила, чтобы структура не расползалась

- Один тип на файл, имя файла = имя типа. Исключение: вложенные типы (`Dream.Run`).
- `Core/` не ссылается на `Dream/` и `UI/`. (Сейчас `Brain` знает про `Ragdoll`, это известная связь.)
- Новый чип: файл в `Chips/`, класс `IChip`, слот выбирается через `LobeKind`.
- Новая учебная задача: `XxxTask` + `XxxEpisode` в `Dream/Tasks/Xxx/`, регистрация в `DreamTasks.Make`.
- Новые аргументы командной строки сна: `Dream/Dream.Args.cs`. Проба выносливости: `Dream/Dream.Probe.cs`.
- Класс-скрипт Godot (`: Node2D`, `: Control`) лежит в файле с тем же именем, `partial`.

## Сборка и проверка

Godot: `C:\Users\fbits\OneDrive\Desktop\Godot.NET\Godot_v4.7.2-stable_mono_win64_console.exe`. Агент запускает проверки сам после каждого Fix.

**Важно:** PowerShell съедает голый `--` перед передачей в скрипт. Всегда кавычьте его: `'--'`.

```
dotnet build "New Game Project.sln"

# Базовый тест сна (2 поколения)
./tools/godot.ps1 --headless --fixed-fps 30 --path . res://Dream.tscn '--' --gens 2 --fresh --pristine --sector STAND --chip user://chips/test.chip

# Тест ROM без CP-шага (голеностоп + бёдра)
./tools/godot.ps1 --headless --fixed-fps 30 --path . res://Dream.tscn '--' --sector RECOVER --motor-rom-step-test --levels '0.5,0.6,0.7'

# Тест ROM с CP-шагом (экспериментально, --cp-step)
./tools/godot.ps1 --headless --fixed-fps 30 --path . res://Dream.tscn '--' --sector RECOVER --motor-rom-step-test --cp-step --levels '0.5,0.6,0.7'

# Тест ROM шага с нестандартным пределом бедра
./tools/godot.ps1 --headless --fixed-fps 30 --path . res://Dream.tscn '--' --sector RECOVER --motor-rom-step-test --cp-step --levels '0.5,0.6,0.7' --hip-upper 0.7

# Проба выносливости (ROM, 60 с)
./tools/godot.ps1 --headless --fixed-fps 30 --path . res://Dream.tscn '--' --probe 60 --motor rom --wear off --gyro off

# Проба выносливости (NN)
./tools/godot.ps1 --headless --fixed-fps 30 --path . res://Dream.tscn '--' --probe 60 --motor nn --wear off --gyro on
```

Тестовый чип после проверки удалять. Игровые чипы: `%APPDATA%\Godot\app_userdata\RealAndroid\chips\`.
Логи сна: `%APPDATA%\Godot\app_userdata\RealAndroid\logs\` и зеркало `res://Diagnostics/DreamLogs/` (только при запуске из проекта).

**Ночной сон:** `tools\dream_recover.bat [поколений] [чип] [nogit]`
По умолчанию 200 поколений, чип `user://chips/motor_nn.chip`. После сна автоматически коммитит и пушит лог.
Пример пробного прогона (3 поколения, без git): `tools\dream_recover.bat 3 user://chips/recover_trial.chip nogit`

## Известные кандидаты на дальнейший рефакторинг (поведение не менялось)

- `Dream/Dream.cs` (~560 строк): поля пробы и протокольного теста можно вынести в отдельные partial-файлы или классы.
- `Chips/MotorRom.cs` (~470 строк) и `Body/Ragdoll.cs` (~355 строк): крупные, внутри есть естественные швы.
- `StandEpisode` и `RecoverEpisode` дублируют расчёт позы (`_ref`, `_jw`, `_jwSum`); `Dream.ProbePose` делает то же.
- `Main.cs` использует табуляцию, остальные файлы пробелы и CRLF.
