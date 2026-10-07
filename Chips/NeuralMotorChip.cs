using System;
using System.Linq;

public readonly record struct MotorSpec(int In, int H1, int H2, int Out, int Sectors, uint IoHash);

/// Моторная кора: один чип, общая сеть + сектора протоколов (p — вектор арбитра).
/// h1 = tanh(W1·x + b1 + E·p) ⊙ (1 + G·p);  h2 = tanh(W2·h1 + b2);  o = W3·h2 + b3 + O·p
/// target = tanh(o) в диапазон сустава (0 → 0 рад), stiffness = sigmoid(o − StiffBias)
public sealed class NeuralMotorChip : IChip
{
    public const float StiffBias = 2.5f;
    public const string Format = "motor_v4";
    const string IoMode = "residual:rom";

    public LobeKind Kind => LobeKind.Motor;
    public string Label => Burnt ? $"{Name} СГОРЕЛ" : $"{Name} g{Generation}";

    public readonly MotorSpec Spec;
    public readonly float[] W;
    public readonly float[] Maturity;
    public readonly int[] SectorGen;
    public string Name = "MOTOR NN";
    public int Generation;
    public bool Burnt;
    public bool BaseStepping = false;
    public float BaseStepAssist = 1f;
    public float ResTargetUpper = 0.5f, ResTargetLeg = 0.2f, ResTargetSwing = 0.1f, ResTau = 0.06f;
    public float ResStiff = 0.4f;

    readonly int _oW1, _oB1, _oE, _oG, _oW2, _oB2, _oW3, _oB3, _oO, _count;
    readonly float[] _x, _h1, _h2, _o;
    readonly float[] _baseT, _baseS, _rt, _rs;
    readonly bool[] _isLeg;
    readonly MotorRom _base = new();
    float[] _pain, _lo, _hi;

    // вход: углы, скорости, vest.balance, pain.total[3], cmd[4]
    public static int InputSize(int nj) => 2 * nj + Bal.Size + 3 + 4;

    public static MotorSpec SpecFor(RagdollDef d, int h1 = 32, int h2 = 32)
    {
        int nj = d.Joints.Count;
        int i = InputSize(nj), o = 2 * nj, s = Protocol.Count;
        return new MotorSpec(i, h1, h2, o, s, IoHash(d, i, o, s));
    }

    public static uint IoHash(RagdollDef d, int i, int o, int s)
    {
        string str = $"{Format}|{IoMode}|{i}|{o}|{s}|{Bal.Size}|" + string.Join(",", d.Joints.Select(j => j.Name));
        uint h = 2166136261;
        foreach (char c in str) { h ^= c; h *= 16777619; }
        return h;
    }

    public static int ParamCount(MotorSpec s) =>
        s.H1 * s.In + s.H1 + 2 * s.H1 * s.Sectors + s.H2 * s.H1 + s.H2 + s.Out * s.H2 + s.Out + s.Out * s.Sectors;

    public NeuralMotorChip(MotorSpec s, float[] w)
    {
        _count = ParamCount(s);
        if (w.Length != _count) throw new ArgumentException($"weights {w.Length} != {_count}");
        Spec = s; W = w;
        Maturity = new float[s.Sectors];
        SectorGen = new int[s.Sectors];
        int k = 0;
        _oW1 = k; k += s.H1 * s.In;
        _oB1 = k; k += s.H1;
        _oE  = k; k += s.H1 * s.Sectors;
        _oG  = k; k += s.H1 * s.Sectors;
        _oW2 = k; k += s.H2 * s.H1;
        _oB2 = k; k += s.H2;
        _oW3 = k; k += s.Out * s.H2;
        _oB3 = k; k += s.Out;
        _oO  = k;
        _x = new float[s.In]; _h1 = new float[s.H1]; _h2 = new float[s.H2]; _o = new float[s.Out];
        _baseT = new float[s.Out / 2]; _baseS = new float[s.Out / 2];
        _rt = new float[s.Out / 2]; _rs = new float[s.Out / 2];
        _isLeg = new bool[s.Out / 2];
    }

    public NeuralMotorChip CloneWith(float[] w)
    {
        var c = new NeuralMotorChip(Spec, w) { Name = Name, Generation = Generation };
        Array.Copy(Maturity, c.Maturity, Maturity.Length);
        Array.Copy(SectorGen, c.SectorGen, SectorGen.Length);
        return c;
    }

    /// Пустой чип: случайные скрытые слои, нулевые выход и сектора → обмякла
    public static NeuralMotorChip Blank(RagdollDef d, int seed, int h1 = 32, int h2 = 32)
    {
        var s = SpecFor(d, h1, h2);
        var w = new float[ParamCount(s)];
        var c = new NeuralMotorChip(s, w) { Name = "BLANK" };
        var r = new Random(seed);
        float s1 = 1f / MathF.Sqrt(s.In), s2 = 1f / MathF.Sqrt(s.H1);
        for (int i = 0; i < s.H1 * s.In; i++) w[c._oW1 + i] = Rng.Gauss(r) * s1;
        for (int i = 0; i < s.H2 * s.H1; i++) w[c._oW2 + i] = Rng.Gauss(r) * s2;
        Array.Clear(w, c._oW3, s.Out * s.H2);
        Array.Clear(w, c._oB3, s.Out);
        Array.Clear(w, c._oO, s.Out * s.Sectors);
        return c;
    }

    /// Маска обучения: свой сектор — 1, чужие — 0, общие веса — shared
    public float[] Mask(int sector, float shared)
    {
        var m = new float[_count];
        Array.Fill(m, shared);
        var s = Spec;
        for (int h = 0; h < s.H1; h++)
            for (int q = 0; q < s.Sectors; q++)
            {
                float v = q == sector ? 1f : 0f;
                m[_oE + h * s.Sectors + q] = v;
                m[_oG + h * s.Sectors + q] = v;
            }
        for (int o = 0; o < s.Out; o++)
            for (int q = 0; q < s.Sectors; q++)
                m[_oO + o * s.Sectors + q] = q == sector ? 1f : 0f;
        return m;
    }

    public void Reset(Brain b)
    {
        Array.Clear(_rt);
        Array.Clear(_rs);
        _base.Reset(b);
        b.Bus.Channels.TryGetValue("pain.total", out _pain);
        var joints = b.Body.Def.Joints;
        int nj = joints.Count;
        _lo = new float[nj]; _hi = new float[nj];
        for (int j = 0; j < nj; j++)
        {
            _lo[j] = joints[j].Lower;
            _hi[j] = joints[j].Upper;
            string name = joints[j].Name;
            _isLeg[j] = name.StartsWith("hip") || name.StartsWith("knee") || name.StartsWith("ankle");
        }
    }

    public void Tick(Brain b, float dt)
    {
        _base.CapturePointStepping = BaseStepping;
        _base.CapturePointAssist = BaseStepAssist;
        _base.Tick(b, dt);
        Array.Copy(b.MotorTarget, _baseT, _baseT.Length);
        Array.Copy(b.MotorStiff, _baseS, _baseS.Length);

        var s = Spec;
        int nj = s.Out / 2;
        if (Burnt || _lo == null || nj != b.MotorTarget.Length || s.In != InputSize(nj)) return;

        int k = 0;
        for (int j = 0; j < nj; j++) _x[k++] = b.Angle[j];
        for (int j = 0; j < nj; j++) _x[k++] = b.Vel[j];
        for (int i = 0; i < Bal.Size; i++) _x[k++] = b.Balance[i];
        for (int i = 0; i < 3; i++) _x[k++] = _pain != null ? _pain[i] : 0f;
        for (int i = 0; i < 4; i++) _x[k++] = b.Cmd[i];

        var p = b.P;
        int S = s.Sectors;

        for (int h = 0; h < s.H1; h++)
        {
            float a = W[_oB1 + h], g = 1f;
            int row = _oW1 + h * s.In;
            for (int i = 0; i < s.In; i++) a += W[row + i] * _x[i];
            int e = _oE + h * S, gg = _oG + h * S;
            for (int q = 0; q < S; q++) { a += W[e + q] * p[q]; g += W[gg + q] * p[q]; }
            _h1[h] = MathF.Tanh(a) * g;
        }
        for (int h = 0; h < s.H2; h++)
        {
            float a = W[_oB2 + h];
            int row = _oW2 + h * s.H1;
            for (int i = 0; i < s.H1; i++) a += W[row + i] * _h1[i];
            _h2[h] = MathF.Tanh(a);
        }
        for (int o = 0; o < s.Out; o++)
        {
            float a = W[_oB3 + o];
            int row = _oW3 + o * s.H2;
            for (int h = 0; h < s.H2; h++) a += W[row + h] * _h2[h];
            int oo = _oO + o * S;
            for (int q = 0; q < S; q++) a += W[oo + q] * p[q];
            _o[o] = a;
        }

        for (int j = 0; j < nj; j++)
        {
            float smoothing = Math.Min(1f, dt / Math.Max(ResTau, 1e-6f));
            float targetRaw = MathF.Tanh(_o[j]);
            float stiffRaw = MathF.Tanh(_o[nj + j]);
            _rt[j] += (targetRaw - _rt[j]) * smoothing;
            _rs[j] += (stiffRaw - _rs[j]) * smoothing;
            float targetScale = b.SwingingJoints[j] ? ResTargetSwing :
                _isLeg[j] ? ResTargetLeg : ResTargetUpper;
            float targetResidual = targetScale * _rt[j];
            float stiffResidual = ResStiff * _rs[j];
            if (!float.IsFinite(targetResidual) || !float.IsFinite(stiffResidual))
            {
                Burnt = true;
                Array.Copy(_baseT, b.MotorTarget, _baseT.Length);
                Array.Copy(_baseS, b.MotorStiff, _baseS.Length);
                return;
            }
            b.MotorTarget[j] = Math.Clamp(_baseT[j] + targetResidual, _lo[j], _hi[j]);
            b.MotorStiff[j] = b.SwingingJoints[j]
                ? _baseS[j]
                : Math.Clamp(_baseS[j] + stiffResidual, 0f, 1f);
        }
    }
}
