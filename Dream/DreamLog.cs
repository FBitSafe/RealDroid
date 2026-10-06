using Godot;
using System;

public static class DreamLog
{
    const string LogDir = "user://logs";
    const string MirrorDir = "res://Diagnostics/DreamLogs";
    static string _path = "user://dream_log.txt";   // fallback until Begin() is called
    static string _mirrorPath = null;

    /// Call once at the start of each dream to open a fresh per-dream log file.
    public static void Begin(string sectorName)
    {
        DirAccess.MakeDirRecursiveAbsolute(LogDir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string name = $"dream_{stamp}_{sectorName}.txt";
        _path = $"{LogDir}/{name}";

        // Mirror into project only when running from editor/source (not an exported template)
        if (!OS.HasFeature("template"))
        {
            DirAccess.MakeDirRecursiveAbsolute(MirrorDir);
            _mirrorPath = $"{MirrorDir}/{name}";
        }
        else
            _mirrorPath = null;
    }

    public static void Line(string s)
    {
        GD.Print(s);
        Append(_path, s);
        if (_mirrorPath != null) try { Append(_mirrorPath, s); } catch { /* mirror errors are silent */ }
    }

    static void Append(string path, string s)
    {
        using var f = FileAccess.FileExists(path)
            ? FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.SeekEnd();
        f.StoreLine(s);
    }
}
