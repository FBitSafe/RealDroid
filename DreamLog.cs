using Godot;

public static class DreamLog
{
    const string Path = "user://dream_log.txt";
    public static void Line(string s)
    {
        GD.Print(s);
        using var f = FileAccess.FileExists(Path)
            ? FileAccess.Open(Path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(Path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.SeekEnd();
        f.StoreLine(s);
    }
}
