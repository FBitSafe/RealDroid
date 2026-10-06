using System;

/// Ноцицепторы суставов (железо, общий кабель с энкодерами).
/// Discomfort — край хода, тёплая катушка. Привыкает. Рефлекса нет.
/// Ache       — давление и удар в упор, скрутка, горячая катушка, фантомы, сломанный сустав за пределом в покое. Отдёргивание.
/// Acute      — поломка упора, разрыв трубки, обрыв кабеля, КЗ, критический перегрев, движение сломанного сустава за пределом. Реакция всего тела.
/// Каждый уровень 0..1 на сустав. Total: [0] дискомфорт, [1] боль, [2] острая — по телу.
public sealed class Nociception
{
    // дискомфорт
    public float ComfortZone = 0.05f;          // рад до упора
    public float EdgeGain = 0.3f;
    public float WarmStart = 60f, WarmSpan = 25f, WarmGain = 0.5f;
    public float HabituationTau = 4f, Habituation = 0.8f;
    // боль
    public float StopFree = 0.03f, StopGain = 6f;   // лёгкий контакт с упором не болит
    public float PinchGain = 1.5f;
    public float HeatStart = 85f;
    public float PhantomRate = 3f;
    public float Sensitization = 1f, SensitizeTau = 8f;
        // острая
    public float ImpactSpeed = 4f, ImpactSpan = 8f; // рад/с в упор
    public float ShortJolt = 1f, BreakJolt = 1.5f, HoseJolt = 0.6f;
    public float ImpactAche = 1f;            // удар в упор: обычная боль
    public float OverAche = 1.5f;            // сломанный сустав за пределом: ноет (на рад)
    public float OverAcute = 2f, OverVel = 2f; // ...и острая при движении (рад/с для полной силы)
    // затухание
    public float DiscomfortDecay = 0.5f, AcheDecay = 0.4f, AcuteDecay = 0.15f;

    public readonly float[] Discomfort, Ache, Acute, Limit, Side, Total;

    readonly Ragdoll _b;
    readonly float[] _hab, _sens;
    readonly bool[] _wasBroken, _wasCut, _wasHose;
    readonly Random _rng = new(17);

    public Nociception(Ragdoll b, Brain br)
    {
        _b = b;
        int nj = b.Def.Joints.Count;
        Discomfort = br.Bus.Add("pain.discomfort", nj);
        Ache       = br.Bus.Add("pain.ache", nj);
        Acute      = br.Bus.Add("pain.acute", nj);
        Limit      = br.Bus.Add("pain.limit", nj);
        Side       = br.Bus.Add("pain.side", nj);
        Total      = br.Bus.Add("pain.total", 3);
        _hab = new float[nj]; _sens = new float[nj];
        _wasBroken = new bool[nj]; _wasCut = new bool[nj]; _wasHose = new bool[nj];
    }

    /// 0 — нет, 0..1 дискомфорт, 1..2 боль, 2..3 острая
    public float Level(int j) =>
        Acute[j] > 0.05f ? 2f + Acute[j] : Ache[j] > 0.05f ? 1f + Ache[j] : Discomfort[j];

    public void Tick(float dt)
    {
        var b = _b;
        var d = b.Dur;
        float kD = MathF.Exp(-dt / DiscomfortDecay), kA = MathF.Exp(-dt / AcheDecay);
        float kX = MathF.Exp(-dt / AcuteDecay), kS = MathF.Exp(-dt / SensitizeTau);
        int nj = Ache.Length;
        float maxD = 0, maxA = 0, maxX = 0, sumD = 0, sumA = 0, sumX = 0;

        for (int j = 0; j < nj; j++)
        {
            float dr = 0f, ar = 0f, xr = 0f, lr = 0f;

            bool broken = d.StopBroken(j);
            bool cut = d.Effects && d.CableCut[j];
            bool hose = d.Effects && d.HoseTorn[j];
            if (broken && !_wasBroken[j]) xr += BreakJolt;
            if (cut && !_wasCut[j]) xr += BreakJolt;      // последний сигнал перед онемением
            if (hose && !_wasHose[j]) xr += HoseJolt;
            _wasBroken[j] = broken; _wasCut[j] = cut; _wasHose[j] = hose;

            if (!cut)
            {
                var jd = b.Def.Joints[j];
                float a = b.Angle[j];
                float lo = a - jd.Lower, hi = jd.Upper - a;
                float edge = MathF.Min(lo, hi);
                float side = lo < hi ? -1f : 1f;

                                if (!broken)
                {
                    if (edge < ComfortZone)
                    {
                        float e = Math.Clamp((ComfortZone - edge) / ComfortZone, 0f, 1f);
                        dr += EdgeGain * e * e;
                    }
                    if (edge < 0f)
                    {
                        float into = side * b.AngVel[j];
                        if (into > ImpactSpeed)
                        {
                            float x = (into - ImpactSpeed) / ImpactSpan;
                            ar += ImpactAche * x; lr += x; Side[j] = side;
                        }
                        float over = -edge - StopFree;
                        if (over > 0f)
                        {
                            float p = StopGain * over * (1f + d.StopWear[j]);
                            ar += p; lr += p; Side[j] = side;
                        }
                    }
                }
                else if (edge < 0f)
                {
                    float over = -edge;
                    float p = OverAche * over;
                    ar += p; lr += p; Side[j] = side;
                    float move = Math.Min(1f, MathF.Abs(b.AngVel[j]) / OverVel);
                    xr += OverAcute * over * move;
                }

                if (d.Effects)
                {
                    float turns = MathF.Abs(d.Twist[j]);
                    if (turns > d.TwistFree)
                    {
                        float p = PinchGain * (turns - d.TwistFree);
                        ar += p; lr += p; Side[j] = MathF.Sign(d.Twist[j]);
                        if (turns > d.HoseTurns) xr += 0.5f * (turns - d.HoseTurns);
                    }
                    if (d.ShortTime[j] > 0f) xr += ShortJolt;

                    float bare = Math.Clamp((d.ShortThreshold - d.Insulation[j]) / d.ShortThreshold, 0f, 1f);
                    if (bare > 0f && _rng.NextDouble() < PhantomRate * bare * dt)
                    {
                        float p = 0.4f + 0.6f * (float)_rng.NextDouble();
                        ar += p; lr += p; Side[j] = _rng.Next(2) * 2 - 1;
                    }
                }

                float T = b.Temp[j];
                if (T > WarmStart) dr += WarmGain * Math.Clamp((T - WarmStart) / WarmSpan, 0f, 1f);
                if (T > HeatStart) ar += (T - HeatStart) / Math.Max(b.CritTemp - HeatStart, 1f);
                if (T > b.CritTemp) xr += (T - b.CritTemp) / 20f;
            }

            // привыкание к дискомфорту
            _hab[j] += (dr - _hab[j]) * Math.Min(1f, dt / HabituationTau);
            dr = MathF.Max(0f, dr - Habituation * _hab[j]);

            // сенсибилизация: после острой боли сустав болит сильнее
            _sens[j] = MathF.Max(_sens[j] * kS, MathF.Min(1f, xr));
            float sens = 1f + Sensitization * _sens[j];
            ar *= sens; lr *= sens;

            Discomfort[j] = MathF.Min(1f, MathF.Max(dr, Discomfort[j] * kD));
            Ache[j]       = MathF.Min(1f, MathF.Max(ar, Ache[j] * kA));
            Acute[j]      = MathF.Min(1f, MathF.Max(xr, Acute[j] * kX));
            Limit[j]      = MathF.Min(1f, MathF.Max(lr, Limit[j] * kA));

            maxD = MathF.Max(maxD, Discomfort[j]); sumD += Discomfort[j];
            maxA = MathF.Max(maxA, Ache[j]);       sumA += Ache[j];
            maxX = MathF.Max(maxX, Acute[j]);      sumX += Acute[j];
        }

        Total[0] = MathF.Min(1f, maxD + 0.2f * sumD / nj);
        Total[1] = MathF.Min(1f, maxA + 0.2f * sumA / nj);
        Total[2] = MathF.Min(1f, maxX + 0.2f * sumX / nj);
    }
}
