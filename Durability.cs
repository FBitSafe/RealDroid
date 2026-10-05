using Godot;
using System;
using System.Collections.Generic;

public enum DamageMode { Off, Frozen, Live }

/// Износ упоров, проводки и трубок хладагента.
/// Off — идеальное тело, Frozen — повреждения действуют, но не растут, Live — всё.
public sealed class Durability
{
    public DamageMode Mode = DamageMode.Live;
    public bool Accumulate => Mode == DamageMode.Live;
    public bool Effects => Mode != DamageMode.Off;

    // ── упоры ──
    public float StopTolerance = 0.06f;   // рад перехода за упор без износа (у нового)
    public float StopWearRate  = 3f;      // износ за рад·с сверх допуска
    public float Brittleness   = 2f;      // во сколько раз быстрее изнашивается изношенный

    // ── проводка ──
    public float TwistFree      = 0.25f;  // оборотов скрутки без трения
    public float InsulationRate = 0.015f; // износ изоляции за (рад пути × оборот скрутки)
    public float HoseTurns      = 2.5f;
    public float CableTurns     = 5f;
    public float CrossTalk      = 0.6f;   // наводка тока привода на датчик при голой изоляции, рад
    public float ShortThreshold = 0.6f;   // ниже — начинаются КЗ
    public float ShortRate      = 4f;     // КЗ в секунду при полностью голых проводах

    // ── хладагент ──
    public float LeakRate    = 0.02f;     // доля запаса в секунду на одну порванную трубку
    public float HoseCooling = 0.15f;     // охлаждение катушки с порванной трубкой

    public readonly float[] StopWear, Twist, Insulation, ShortTime;
    public readonly bool[] HoseTorn, CableCut;
    public float Coolant = 1f;
    public readonly int[][] Route;        // Route[j] — суставы, чьи провода проходят через j

    public event Action<string> Log;

    readonly Ragdoll _b;
    readonly float[] _held, _shortAngle, _shortCurrent;
    readonly Random _rng = new(11);

    public Durability(Ragdoll b)
    {
        _b = b;
        int nj = b.Def.Joints.Count, np = b.Def.Parts.Count;
        StopWear = new float[nj]; Twist = new float[nj];
        Insulation = new float[nj]; ShortTime = new float[nj];
        HoseTorn = new bool[nj]; CableCut = new bool[nj];
        _held = new float[nj]; _shortAngle = new float[nj]; _shortCurrent = new float[nj];
        Array.Fill(Insulation, 1f);

        // провода идут от ядра (таза) наружу: кабель локтя проходит через плечо и позвоночник
        var jointOfChild = new int[np];
        Array.Fill(jointOfChild, -1);
        for (int j = 0; j < nj; j++) jointOfChild[b.JointChild(j)] = j;

        Route = new int[nj][];
        for (int j = 0; j < nj; j++)
        {
            var list = new List<int>();
            for (int k = 0; k < nj; k++)
                for (int q = k; q >= 0; q = jointOfChild[b.JointParent(q)])
                    if (q == j) { list.Add(k); break; }
            Route[j] = list.ToArray();
        }
    }

    string N(int j) => _b.Def.Joints[j].Name;

    // ───────── каждый физический такт, после чтения датчиков ─────────
    public void Tick(float dt)
    {
        if (!Effects) return;
        var b = _b;
        int nj = Twist.Length;

        for (int j = 0; j < nj; j++)
        {
            var jd = b.Def.Joints[j];
            float a = b.Angle[j];
            float excess = a < jd.Lower ? a - jd.Lower : a > jd.Upper ? a - jd.Upper : 0f;
            Twist[j] = excess / Mathf.Tau;              // обороты сверх хода сустава, со знаком
            if (!Accumulate) continue;

            float turns = MathF.Abs(Twist[j]);
            if (turns <= TwistFree) continue;

            float rub = InsulationRate * (turns - TwistFree) * MathF.Abs(b.AngVel[j]) * dt;
            foreach (int k in Route[j])
            {
                float before = Insulation[k];
                Insulation[k] = MathF.Max(0f, before - rub);
                if (before >= ShortThreshold && Insulation[k] < ShortThreshold)
                    Log?.Invoke($"Повреждена изоляция: {N(k)}");
                if (turns > HoseTurns && !HoseTorn[k])
                { HoseTorn[k] = true; Log?.Invoke($"Порвана трубка хладагента: {N(k)}"); }
                if (turns > CableTurns && !CableCut[k])
                { CableCut[k] = true; Log?.Invoke($"Оборван кабель: {N(k)}"); }
            }
        }

        if (Accumulate)
        {
            int torn = 0;
            foreach (bool t in HoseTorn) if (t) torn++;
            Coolant = MathF.Max(0f, Coolant - LeakRate * torn * dt);
        }

        // короткие замыкания
        for (int j = 0; j < nj; j++)
        {
            if (CableCut[j]) { ShortTime[j] = 0f; continue; }
            if (ShortTime[j] > 0f) { ShortTime[j] -= dt; continue; }
            float bare = Math.Clamp((ShortThreshold - Insulation[j]) / ShortThreshold, 0f, 1f);
            if (HoseTorn[j] && Coolant > 0f) bare = MathF.Min(1f, bare * 2f);   // хладагент на голых проводах
            if (bare <= 0f || _rng.NextDouble() > ShortRate * bare * dt) continue;
            ShortTime[j]     = 0.05f + 0.25f * (float)_rng.NextDouble();
            _shortAngle[j]   = ((float)_rng.NextDouble() * 2f - 1f) * Mathf.Pi;
            _shortCurrent[j] = ((float)_rng.NextDouble() * 2f - 1f) * 1.5f;
        }
    }

    // ───────── датчики: истина → то, что видит мозг ─────────
    public void Sense(Brain br)
    {
        var b = _b;
        for (int j = 0; j < Twist.Length; j++)
        {
            float a = b.Angle[j], v = b.AngVel[j] * 0.1f;
            if (Effects)
            {
                if (CableCut[j]) { a = _held[j]; v = 0f; }          // датчик мёртв, застыл
                else
                {
                    float bare = 1f - Insulation[j];
                    a += CrossTalk * bare * bare * b.Current[j];    // наводка от привода
                    if (ShortTime[j] > 0f) { a = _shortAngle[j]; v = (float)_rng.NextDouble() * 4f - 2f; }
                }
            }
            if (!CableCut[j] || !Effects) _held[j] = a;
            br.Angle[j] = a;
            br.Vel[j] = v;
        }
    }

    // ───────── привод ─────────
    public float Drive(int j, float cmd, float iAvail)
    {
        if (!Effects) return cmd;
        if (CableCut[j]) return 0f;
        if (ShortTime[j] > 0f) return Math.Clamp(_shortCurrent[j], -iAvail, iAvail);
        return cmd;
    }

    public float Cooling(int j) => !Effects ? 1f
        : (HoseTorn[j] ? HoseCooling : 1f) * (0.2f + 0.8f * Coolant);

    // ───────── упоры ─────────
    public bool StopBroken(int j) => Effects && StopWear[j] >= 1f;
    public float StopFactor(int j) => !Effects ? 1f : MathF.Max(0f, 1f - StopWear[j]);

    public void OnStop(int j, float over, float dt)
    {
        if (!Accumulate || StopWear[j] >= 1f) return;
        float w = StopWear[j];
        float d = MathF.Abs(over) - StopTolerance * (1f - w);   // хрупкий упор ломается от меньшего
        if (d <= 0f) return;
        float nw = MathF.Min(1f, w + StopWearRate * (1f + Brittleness * w) * d * dt);
        StopWear[j] = nw;
        if (w < 0.5f && nw >= 0.5f) Log?.Invoke($"Трещина в упоре: {N(j)}");
        if (nw >= 1f) Log?.Invoke($"Упор сломан: {N(j)}");
    }

    // ───────── сервис ─────────
    public void AgeStops(float amount)
    {
        for (int j = 0; j < StopWear.Length; j++) StopWear[j] = MathF.Min(1f, StopWear[j] + amount);
    }

    public void Repair()
    {
        Array.Clear(StopWear); Array.Clear(Twist); Array.Fill(Insulation, 1f);
        Array.Clear(ShortTime); Array.Clear(HoseTorn); Array.Clear(CableCut);
        Coolant = 1f;
        _b.UntwistAngles();
    }

    public void CopyFrom(Durability o)   // для сна: перенести травмы в клон
    {
        Array.Copy(o.StopWear, StopWear, StopWear.Length);
        Array.Copy(o.Insulation, Insulation, Insulation.Length);
        Array.Copy(o.HoseTorn, HoseTorn, HoseTorn.Length);
        Array.Copy(o.CableCut, CableCut, CableCut.Length);
        Coolant = o.Coolant;
    }
}
