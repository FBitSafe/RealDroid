using Godot;
using System;

public static class ChipFile
{
    public const string MotorPath = "user://chips/motor_nn.chip";
    const uint Magic = 0x50494843, Version = 2;   // "CHIP"

    public static bool Save(string path, NeuralMotorChip c)
    {
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        string tmp = path + ".tmp";
        using (var f = FileAccess.Open(tmp, FileAccess.ModeFlags.Write))
        {
            if (f == null) { GD.PushError($"Не удалось записать {tmp}: {FileAccess.GetOpenError()}"); return false; }
            var s = c.Spec;
            f.Store32(Magic); f.Store32(Version); f.Store32((uint)c.Kind);
            f.Store32((uint)s.In); f.Store32((uint)s.H1); f.Store32((uint)s.H2);
            f.Store32((uint)s.Out); f.Store32((uint)s.Sectors); f.Store32(s.IoHash);
            f.Store32((uint)c.Generation);
            f.StorePascalString(c.Name);
            for (int k = 0; k < s.Sectors; k++) { f.StoreFloat(c.Maturity[k]); f.Store32((uint)c.SectorGen[k]); }
            f.Store32((uint)c.W.Length);
            foreach (var w in c.W) f.StoreFloat(w);
        }
        if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
        return DirAccess.RenameAbsolute(tmp, path) == Error.Ok;
    }

    /// null — файла нет, формат старый или разъём не подходит к телу
    public static NeuralMotorChip LoadMotor(string path, RagdollDef def)
        => LoadMotor(path, def, out _);

    public static NeuralMotorChip LoadMotor(string path, RagdollDef def, out bool incompatibleIoHash)
    {
        incompatibleIoHash = false;
        if (!FileAccess.FileExists(path)) return null;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null || f.Get32() != Magic || f.Get32() != Version) return null;
        if (f.Get32() != (uint)LobeKind.Motor) return null;

        var s = new MotorSpec((int)f.Get32(), (int)f.Get32(), (int)f.Get32(),
                              (int)f.Get32(), (int)f.Get32(), f.Get32());
        var expected = NeuralMotorChip.SpecFor(def, s.H1, s.H2);
        incompatibleIoHash = s.IoHash != expected.IoHash;
        if (s != expected)
        {
            GD.PushWarning($"{path.GetFile()}: несовместимый IO hash или разъём");
            return null;
        }

        int gen = (int)f.Get32();
        string name = f.GetPascalString();
        var mat = new float[s.Sectors];
        var sg = new int[s.Sectors];
        for (int k = 0; k < s.Sectors; k++) { mat[k] = f.GetFloat(); sg[k] = (int)f.Get32(); }

        int n = (int)f.Get32();
        if (n != NeuralMotorChip.ParamCount(s)) return null;
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = f.GetFloat();

        var c = new NeuralMotorChip(s, w) { Name = name, Generation = gen };
        Array.Copy(mat, c.Maturity, mat.Length);
        Array.Copy(sg, c.SectorGen, sg.Length);
        return c;
    }
}
