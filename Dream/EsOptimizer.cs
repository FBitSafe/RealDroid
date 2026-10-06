using System;
using System.Linq;

/// OpenAI-ES: антитетические пары, ранги, Adam. Scale — множитель шума и шага на параметр (0 = заморожен).
public sealed class EsOptimizer
{
    public readonly float[] Theta;
    public float[] Scale;
    readonly int _n, _pairs;
    readonly float _sigma, _lr;
    readonly float[] _m, _v;
    float[][] _eps;
    int _t;
    readonly Random _rng;

    public int Population => _pairs * 2;

    public EsOptimizer(float[] init, int pairs, float sigma, float lr, int seed)
    {
        Theta = (float[])init.Clone(); _n = init.Length;
        _pairs = pairs; _sigma = sigma; _lr = lr;
        _m = new float[_n]; _v = new float[_n];
        _rng = new Random(seed);
    }

    float S(int i) => Scale == null ? 1f : Scale[i];

    public float[][] Ask()
    {
        _eps = new float[_pairs][];
        var c = new float[_pairs * 2][];
        for (int p = 0; p < _pairs; p++)
        {
            var e = new float[_n]; var a = new float[_n]; var b = new float[_n];
            for (int i = 0; i < _n; i++)
            {
                float s = S(i);
                if (s == 0f) { a[i] = b[i] = Theta[i]; continue; }
                float g = Rng.Gauss(_rng);
                e[i] = g;
                a[i] = Theta[i] + _sigma * s * g;
                b[i] = Theta[i] - _sigma * s * g;
            }
            _eps[p] = e; c[2 * p] = a; c[2 * p + 1] = b;
        }
        return c;
    }

    public void Tell(float[] fitness)
    {
        int N = fitness.Length;
        var idx = Enumerable.Range(0, N).OrderBy(i => fitness[i]).ToArray();
        var rank = new float[N];
        for (int r = 0; r < N; r++) rank[idx[r]] = r / (float)(N - 1) - 0.5f;

        var g = new float[_n];
        for (int p = 0; p < _pairs; p++)
        {
            float w = rank[2 * p] - rank[2 * p + 1];
            var e = _eps[p];
            for (int i = 0; i < _n; i++) g[i] += w * e[i];
        }

        _t++;
        const float b1 = 0.9f, b2 = 0.999f, wd = 0.005f;
        float c1 = 1 - MathF.Pow(b1, _t), c2 = 1 - MathF.Pow(b2, _t);
        for (int i = 0; i < _n; i++)
        {
            float s = S(i);
            if (s == 0f) continue;
            float gi = g[i] / (_pairs * _sigma) - wd * Theta[i];
            _m[i] = b1 * _m[i] + (1 - b1) * gi;
            _v[i] = b2 * _v[i] + (1 - b2) * gi * gi;
            Theta[i] += _lr * s * (_m[i] / c1) / (MathF.Sqrt(_v[i] / c2) + 1e-8f);
        }
    }
}