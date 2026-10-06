using Godot;
using System;

public static class DreamLog
{
    const string LogDir = "user://logs";
    static string _path = "user://dream_log.txt";   // fallback until Begin() is called

    /// Call once at the start of each dream to open a fresh per-dream log file.
    public static void Begin(string sectorName)
    {
        DirAccess.MakeDirRecursiveAbsolute(LogDir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        _path = $"{LogDir}/dream_{stamp}_{sectorName}.txt";
    }

    public static void Line(string s)
    {
        GD.Print(s);
        using var f = FileAccess.FileExists(_path)
            ? FileAccess.Open(_path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(_path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.SeekEnd();
        f.StoreLine(s);
    }
}
