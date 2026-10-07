using Godot;

/// Перевод уровня толчка (0..1) в горизонтальный импульс в грудь.
/// dir: 0 — толчок в спину (+x), 1 — толчок в грудь (−x); как в RecoverTask.
public static class PushModel
{
    public const float FullImpulse = 3000f;

    public static void Impulse(float level, int dir, Ragdoll body)
    {
        if (body == null || body.Bodies == null) return;
        float impulse = (dir == 0 ? 1f : -1f) * level * FullImpulse;
        body.Bodies[body.Chest].ApplyCentralImpulse(new Vector2(impulse, 0f));
    }
}