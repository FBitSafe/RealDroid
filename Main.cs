using Godot;
using System;
using System.Collections.Generic;

public partial class Main : Node2D
{
	const float DragK = 120f, DragMaxMass = 15f;
	static readonly float DragD = 2f * Mathf.Sqrt(DragK);

	RagdollDef _def;
	Ragdoll _girl;
	Camera2D _cam;
	Line2D _dragLine;
	RigidBody2D _drag;
	Vector2 _dragLocal;
	BrainPanel _panel;
	LobeKind _sel = LobeKind.Motor;
	DamageMode _damageMode = DamageMode.Live;

	readonly Dictionary<LobeKind, Func<IChip>[]> _variants = new();
	readonly Dictionary<LobeKind, int> _variantIdx = new();
	readonly Dictionary<LobeKind, IChip> _inHand = new();

	public override void _Ready()
	{
		RenderingServer.SetDefaultClearColor(new Color(0.09f, 0.10f, 0.13f));

		var floor = new StaticBody2D
		{
			CollisionLayer = 1, CollisionMask = 0,
			PhysicsMaterialOverride = new PhysicsMaterial { Friction = 1f }
		};
		floor.AddChild(new CollisionShape2D { Shape = new WorldBoundaryShape2D() });
		AddChild(floor);

		_cam = new Camera2D { Zoom = new Vector2(2.5f, 2.5f), Position = new Vector2(0, -90) };
		AddChild(_cam);
		_cam.MakeCurrent();

		_dragLine = new Line2D { Width = 1f, DefaultColor = new Color(1f, 0.8f, 0.3f), ZIndex = 20 };
		AddChild(_dragLine);

		var ui = new CanvasLayer();
		_panel = new BrainPanel { Selected = _sel };
		ui.AddChild(_panel);
		AddChild(ui);

		_variants[LobeKind.Firmware]   = new Func<IChip>[] { () => FirmwareRom.Stock(), () => FirmwareRom.Overclock() };
		_variants[LobeKind.Vestibular] = new Func<IChip>[] { () => VestibularRom.Stock(), () => VestibularRom.Worn() };
		_variants[LobeKind.Reflex]     = new Func<IChip>[] { () => ReflexRom.Stock(), () => ReflexRom.Hyper() };
		_variants[LobeKind.Arbiter]    = new Func<IChip>[] { () => new ArbiterRom(), () => new LockArbiter(Protocol.Stand) };
		_variants[LobeKind.Motor]      = new Func<IChip>[] { () => new MotorRom(), LoadTrainedMotor, () => NeuralMotorChip.Blank(_def, 1) };
		foreach (var k in BrainPanel.Order) _variantIdx[k] = 0;

		_def = RagdollDef.Girl();
		Spawn();
	}

	void Spawn()
	{
		_drag = null;
		_dragLine.ClearPoints();
		_girl?.QueueFree();
		_inHand.Clear();

		_girl = new Ragdoll { Position = new Vector2(_cam.Position.X, -1f) };
		AddChild(_girl);
		_girl.Build(_def, 0);
		_panel.Girl = _girl;
		if (_girl.Broken) return;

		_girl.Dur.Mode = _damageMode;
		_girl.Dur.Log += Say;

		bool waking = DreamJob.Waking;
		if (waking) { DreamJob.Waking = false; _variantIdx[LobeKind.Motor] = 1; }

		foreach (var k in BrainPanel.Order)
			_girl.Brain.Insert(_variants[k][_variantIdx[k]](), instant: true);

		if (waking)
		{
			DreamJob.Damage?.Apply(_girl);
			Say($"Проснулась: {_girl.Brain.Slots[(int)LobeKind.Motor].Chip?.Label}");
		}
	}

	void Say(string m) { _panel.Message = m; _panel.MessageTime = 3f; }

	void Sleep(int sector)
	{
		if (sector != Protocol.Stand)
		{
			var c = ChipFile.LoadMotor(ChipFile.MotorPath, _def, out bool incompatibleIoHash);
			if (incompatibleIoHash)
			{
				const string message = "MOTOR NN: файл v3 несовместим, нужен сон с --fresh";
				GD.Print(message);
				Say(message);
				return;
			}
			if (c == null || c.Maturity[Protocol.Stand] < 0.5f)
			{
				Say($"Сначала доучите STAND: зрелость {(c == null ? 0f : c.Maturity[Protocol.Stand]):P0} < 50%");
				return;
			}
		}
		DreamJob.Sector = sector;
		DreamJob.ChipPath = ChipFile.MotorPath;
		DreamJob.Damage = _girl != null && !_girl.Broken && _girl.Dur != null ? DamageSnapshot.Capture(_girl) : null;
		GetTree().ChangeSceneToFile("res://Dream.tscn");
	}

	IChip LoadTrainedMotor()
	{
		var c = ChipFile.LoadMotor(ChipFile.MotorPath, _def, out bool incompatibleIoHash);
		if (c != null) return c;
		if (incompatibleIoHash)
		{
			const string message = "MOTOR NN: файл v3 несовместим, нужен сон с --fresh";
			GD.Print(message);
			Say(message);
			return new MotorRom();
		}
		var blank = NeuralMotorChip.Blank(_def, 1);
		blank.Name = "BLANK (нет файла)";
		return blank;
	}

	void ToggleSlot()
	{
		var b = _girl?.Brain;
		if (b == null) return;
		var s = b.Slots[(int)_sel];
		if (s.Chip != null)
		{
			var c = b.Eject(_sel);
			_inHand[_sel] = c;
			Say($"Вынут: {c.Label}");
		}
		else
		{
			if (!_inHand.Remove(_sel, out var c)) c = _variants[_sel][_variantIdx[_sel]]();
			b.Insert(c);
			Say($"Вставлен: {c.Label}");
		}
	}

	void NextVariant()
	{
		var b = _girl?.Brain;
		if (b == null) return;
		var list = _variants[_sel];
		_variantIdx[_sel] = (_variantIdx[_sel] + 1) % list.Length;
		b.Eject(_sel);
		_inHand.Remove(_sel);
		var c = list[_variantIdx[_sel]]();
		b.Insert(c);
		Say($"Горячая замена: {c.Label}");
	}

	public override void _Draw()
	{
		DrawRect(new Rect2(-20000, 0, 40000, 2000), new Color(0.16f, 0.17f, 0.21f));
		for (int x = -10000; x <= 10000; x += 50)
			DrawLine(new Vector2(x, 0), new Vector2(x, x % 250 == 0 ? 8 : 4), new Color(0.3f, 0.32f, 0.38f));
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey { Pressed: true, Echo: false } k)
		{
			int idx = (int)k.Keycode - (int)Key.Key1;
			if (idx >= 0 && idx < BrainPanel.Order.Length)
			{
				_sel = BrainPanel.Order[idx];
				_panel.Selected = _sel;
				return;
			}

			switch (k.Keycode)
			{
				case Key.R: Spawn(); break;
				case Key.T: Sleep(Protocol.Stand); break;
				case Key.Y: Sleep(Protocol.Recover); break;
				case Key.Space: ToggleSlot(); break;
				case Key.Tab: NextVariant(); break;
				case Key.G:
					if (_girl == null) break;
					_girl.GyroOn = !_girl.GyroOn;
					Say($"Гироскоп: {(_girl.GyroOn ? "вкл" : "выкл")}");
					break;
				case Key.K:
					_damageMode = (DamageMode)(((int)_damageMode + 1) % 3);
					if (_girl?.Dur != null) _girl.Dur.Mode = _damageMode;
					Say($"Режим износа: {_damageMode}");
					break;
				case Key.L:
					Brain.GetupEnabled = !Brain.GetupEnabled;
					GD.Print($"GETUP {(Brain.GetupEnabled ? "ON" : "OFF")}");
					break;
				case Key.C:
				{
					var motor = _girl?.Brain.Slots[(int)LobeKind.Motor].Chip;
					bool stepping = false;
					bool supported = true;
					switch (motor)
					{
						case MotorRom rom:
							rom.CapturePointStepping = !rom.CapturePointStepping;
							stepping = rom.CapturePointStepping;
							break;
						case NeuralMotorChip nn:
							nn.BaseStepping = !nn.BaseStepping;
							stepping = nn.BaseStepping;
							break;
						default:
							supported = false;
							Say("CP STEP доступен только для MOTOR ROM/NN");
							break;
					}
					if (supported)
					{
						string message = $"CP STEP {(stepping ? "ON" : "OFF")}";
						GD.Print(message);
						Say(message);
					}
					break;
				}
				case Key.J:
					if (_girl?.Dur == null) break;
					_girl.Dur.AgeStops(0.3f);
					Say("Упоры состарены на 30%");
					break;
				case Key.H:
					if (_girl?.Dur == null) break;
					Array.Fill(_girl.Temp, _girl.Ambient);
					Array.Clear(_girl.Damage);
					_girl.Dur.Repair();
					Say("Полный ремонт: катушки, упоры, проводка, хладагент");
					break;
				case Key.Q: Push(-1); break;
				case Key.E: Push(+1); break;
			}
		}
		else if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed) TryGrab(GetGlobalMousePosition());
			else { _drag = null; _dragLine.ClearPoints(); }
		}
	}

	void Push(int dir)
	{
		if (_girl?.Bodies == null) return;
		_girl.Bodies[_girl.Chest].ApplyCentralImpulse(new Vector2(dir * 2500f, 0));
	}

	void TryGrab(Vector2 p)
	{
		if (_girl == null) return;
		var q = new PhysicsPointQueryParameters2D { Position = p, CollisionMask = 0xFFFFFFFE, CollideWithBodies = true };
		var hits = GetWorld2D().DirectSpaceState.IntersectPoint(q, 8);
		RigidBody2D best = null;
		foreach (var h in hits)
			if (h["collider"].AsGodotObject() is RigidBody2D rb && (best == null || rb.ZIndex > best.ZIndex))
				best = rb;
		if (best == null) return;
		_drag = best;
		_dragLocal = best.ToLocal(p);
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_girl != null && _girl.Broken)
		{
			_girl.QueueFree();
			_girl = null; _panel.Girl = null; _drag = null;
			_dragLine.ClearPoints();
			return;
		}

		if (_drag == null || !IsInstanceValid(_drag)) return;
		Vector2 target = GetGlobalMousePosition();
		Vector2 p = _drag.ToGlobal(_dragLocal);
		Vector2 r = p - _drag.GlobalPosition;
		float w = _drag.AngularVelocity;
		Vector2 v = _drag.LinearVelocity + new Vector2(-w * r.Y, w * r.X);
		float m = Math.Min(DragMaxMass, _drag.Mass * 4f);
		Vector2 f = m * (DragK * (target - p) - DragD * v);
		_drag.ApplyForce(f.LimitLength(48f * Ragdoll.Gravity * 2f), r);
		_dragLine.Points = new[] { p, target };
	}

	public override void _Process(double delta)
	{
		if (_girl?.Bodies == null || _girl.Broken) return;
		float dt = (float)delta;
		var chest = _girl.Bodies[_girl.Chest];
		var want = new Vector2(chest.GlobalPosition.X, -90);
		_cam.Position = _cam.Position.Lerp(want, 1f - Mathf.Exp(-3f * dt));
	}
}
