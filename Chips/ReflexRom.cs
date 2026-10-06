using Godot;
using System;
using System.Collections.Generic;

// ───────── РЕФЛЕКСЫ ─────────
public sealed class ReflexRom : IChip
{
    public LobeKind Kind => LobeKind.Reflex;
    public string Label { get; private set; }
    public bool DoNotTouchSwingingLeg = true;
    public float AnkleGain = 4f, AnkleDamp = 0.6f, AnkleMax = 0.8f, HipGain = 0.8f, SpineGain = 0.4f;
    public float Scale = 1f;

    // боль (направленная): отдёргивание внутрь хода
    public float WithdrawGain = 2f, WithdrawStep = 0.3f, WithdrawStiff = 0.6f;
    // острая: оцепенение всего тела
    public float GuardThreshold = 0.05f, GuardFreeze = 0.5f, GuardStiff = 0.5f, FlinchInhibit = 0.3f;
    public float GuardRecoverRelief = 0.7f;                  // насколько ослаблять оцепенение при P[Recover]=1
    public float AnkleEngageTau = 0.12f, AnkleReleaseTau = 0.04f;   // плавное включение / быстрое отпускание, с
    public float SwingKnee = 0.5f, SwingTime = 0.3f;         // рад, с; 0 — выключить
    float[] _ank, _air;
    static readonly Dictionary<int, int> _kneeOf = new();
    // дискомфорт рефлекса не вызывает — он для драйвов и решений

    public static ReflexRom Stock() => new() { Label = "REFLEX ROM 1.2" };
    public static ReflexRom Hyper() => new()
        { Label = "REFLEX x2.5", Scale = 2.5f, WithdrawGain = 5f, GuardStiff = 1f, FlinchInhibit = 0.6f };

    float[] _limit, _side, _total;
    static readonly Dictionary<RagdollDef, int[]> FootByJoint = new();

    public void Reset(Brain b)
    {
        b.Bus.Channels.TryGetValue("pain.limit", out _limit);
        b.Bus.Channels.TryGetValue("pain.side", out _side);
        b.Bus.Channels.TryGetValue("pain.total", out _total);
        if (_ank != null) Array.Clear(_ank);
        if (_air != null) Array.Clear(_air);
    }

    public void Tick(Brain b, float dt)
    {
        var body = b.Body;
        var bl = b.Balance;
        Array.Clear(b.ReflexTarget);
        Array.Clear(b.ReflexStiff);
        b.ReflexInhibit[0] = 0f;

        if (bl[Bal.Lying] < 0.5f)
        {
            int nj = b.ReflexTarget.Length;
            if (_ank == null || _ank.Length != nj) { _ank = new float[nj]; _air = new float[nj]; }
            float off = 0f;
            if (bl[Bal.Support] > 0.5f)
                off = Math.Clamp(Scale * (AnkleGain * bl[Bal.ComX] + AnkleDamp * bl[Bal.ComVx]), -AnkleMax, AnkleMax);
            float pr = Math.Clamp(b.P[Protocol.Recover], 0f, 1f);
            foreach (int a in body.Ankles)
            {
                int ft = FootOf(body, a);
                bool on = body.Grounded[ft] && bl[Bal.Support] > 0.5f
                          && !(DoNotTouchSwingingLeg && b.SwingingJoints[a]);
                float tau = on ? AnkleEngageTau : AnkleReleaseTau;
                float k = Math.Min(1f, dt / Math.Max(tau, 1e-6f));
                _ank[a] += ((on ? 1f : 0f) - _ank[a]) * k;      // _ank — вес включения 0..1
                b.ReflexTarget[a] += _ank[a] * off;             // сигнал баланса без задержки

                // перенос: колено сгибается в первые SwingTime с после отрыва стопы
                _air[a] = body.Grounded[ft] ? 0f : _air[a] + dt;
                int knee = KneeOf(body, a);
                if (SwingKnee > 0f && _air[a] > 0f && _air[a] < SwingTime
                    && knee >= 0 && !b.SwingingJoints[knee])
                    b.ReflexTarget[knee] += SwingKnee * pr * MathF.Sin(MathF.PI * _air[a] / SwingTime);
            }
            foreach (int h in body.Hips)
                if (!DoNotTouchSwingingLeg || !b.SwingingJoints[h])
                    b.ReflexTarget[h] += Scale * HipGain * bl[Bal.PelvisTilt];
            foreach (int s in body.Spine) b.ReflexTarget[s] -= Scale * SpineGain * bl[Bal.ChestTilt];
        }

        if (_limit == null || _side == null) return;
        int n = _limit.Length;
        Span<bool> withdrawn = stackalloc bool[n];
        for (int j = 0; j < n; j++)
        {
            if (DoNotTouchSwingingLeg && b.SwingingJoints[j]) continue;
            float w = Math.Min(1f, _limit[j] * WithdrawGain);
            if (w < 0.01f) continue;
            withdrawn[j] = true;
            float desired = b.Angle[j] - _side[j] * WithdrawStep;
            float delta = desired - b.MotorTarget[j];
            b.ReflexTarget[j] += w * (delta - b.ReflexTarget[j]);
            b.ReflexStiff[j] = Math.Max(b.ReflexStiff[j], w * WithdrawStiff);
        }

        float x = _total != null ? _total[2] : 0f;
        if (x <= GuardThreshold) return;
        float relief = 1f - GuardRecoverRelief * Math.Clamp(b.P[Protocol.Recover], 0f, 1f);
        float f = Math.Min(1f, GuardFreeze * x) * relief;
        for (int j = 0; j < n; j++)
        {
            if (withdrawn[j] || (DoNotTouchSwingingLeg && b.SwingingJoints[j])) continue;
            float hold = b.Angle[j] - b.MotorTarget[j];
            b.ReflexTarget[j] += f * (hold - b.ReflexTarget[j]);
            b.ReflexStiff[j] = Math.Max(b.ReflexStiff[j], GuardStiff * x * relief);
        }
        b.ReflexInhibit[0] = Math.Min(1f, FlinchInhibit * x) * relief;
    }

    static int FootOf(Ragdoll body, int ankleJoint)
    {
        if (!FootByJoint.TryGetValue(body.Def, out var feet))
        {
            feet = new int[body.Def.Joints.Count];
            for (int j = 0; j < feet.Length; j++)
            {
                var joint = body.Def.Joints[j];
                feet[j] = body.Def.PartIndex(joint.Child);
                if (feet[j] < 0)
                    throw new InvalidOperationException($"Joint '{joint.Name}' child '{joint.Child}' has no matching body part.");
                if (joint.Name == "ankle_n" && feet[j] != body.FootNear)
                    throw new InvalidOperationException("ankle_n does not map to FootNear.");
                if (joint.Name == "ankle_f" && feet[j] != body.FootFar)
                    throw new InvalidOperationException("ankle_f does not map to FootFar.");
            }
            FootByJoint.Add(body.Def, feet);
        }
        return feet[ankleJoint];
    }

    static int KneeOf(Ragdoll body, int ankleJoint)
    {
        if (!_kneeOf.TryGetValue(ankleJoint, out int knee))
        {
            string ankleName = body.Def.Joints[ankleJoint].Name;
            const string prefix = "ankle_";
            if (!ankleName.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Joint '{ankleName}' is not an ankle.");
            string kneeName = "knee_" + ankleName.Substring(prefix.Length);
            knee = body.Def.JointIndex(kneeName);
            if (knee < 0)
                throw new InvalidOperationException($"No matching joint '{kneeName}' for '{ankleName}'.");
            _kneeOf.Add(ankleJoint, knee);
        }
        return knee;
    }
}
