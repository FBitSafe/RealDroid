using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;

// Разбор аргументов командной строки (всё, что после "--").
public partial class Dream
{
    void ParseArgs()
    {
        var a = OS.GetCmdlineUserArgs();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--probe":
                    _probeMode = true;
                    if (i + 1 < a.Length &&
                        float.TryParse(a[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) &&
                        float.IsFinite(seconds) && seconds > 0f)
                        _probeSeconds = seconds;
                    else
                    {
                        GD.PushError("--probe requires a positive number of seconds.");
                        _probeInvalid = true;
                    }
                    break;
                case "--motor":
                    if (i + 1 < a.Length && (a[i + 1] == "rom" || a[i + 1] == "nn"))
                        _probeMotor = a[++i];
                    else
                    {
                        GD.PushError("--motor must be rom or nn.");
                        _probeInvalid = true;
                    }
                    break;
                case "--wear":
                    if (i + 1 < a.Length && (a[i + 1] == "off" || a[i + 1] == "live"))
                        _probeLiveWear = a[++i] == "live";
                    else
                    {
                        GD.PushError("--wear must be off or live.");
                        _probeInvalid = true;
                    }
                    break;
                case "--gyro":
                    if (i + 1 < a.Length && (a[i + 1] == "off" || a[i + 1] == "on"))
                        _probeGyro = a[++i] == "on";
                    else
                    {
                        GD.PushError("--gyro must be off or on.");
                        _probeInvalid = true;
                    }
                    break;
                case "--gens":
                    if (i + 1 < a.Length) MaxGenerations = a[++i].ToInt();
                    break;
                case "--cp-step-fade-gens":
                    if (i + 1 < a.Length && int.TryParse(a[++i], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int fadeGenerations) && fadeGenerations >= 0)
                        CpStepFadeGenerations = fadeGenerations;
                    else
                    {
                        GD.PushError("--cp-step-fade-gens must be a non-negative integer.");
                        _probeInvalid = true;
                    }
                    break;
                case "--sector":
                    if (i + 1 < a.Length)
                    {
                        int s = Array.IndexOf(Protocol.Names, a[++i].ToUpper());
                        if (s >= 0) _sector = s; else GD.PushError($"Неизвестный сектор {a[i]}");
                    }
                    break;
                case "--chip":
                    if (i + 1 < a.Length) _chipPath = a[++i];
                    break;
                case "--shared":
                    if (i + 1 < a.Length && float.TryParse(a[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float shared) &&
                        float.IsFinite(shared) && shared >= 0f && shared <= 1f)
                        _sharedOverride = shared;
                    else GD.PushError("--shared must be a number from 0 to 1.");
                    break;
                case "--level":
                    if (i + 1 < a.Length && float.TryParse(a[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float level) &&
                        float.IsFinite(level) && level >= 0f && level <= 1f)
                        _levelOverride = level;
                    else GD.PushError("--level must be a number from 0 to 1.");
                    break;
                case "--min-level":
                    if (i + 1 < a.Length && float.TryParse(a[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float minLevel) &&
                        float.IsFinite(minLevel) && minLevel >= 0f && minLevel <= 1f)
                        _minLevelOverride = minLevel;
                    else GD.PushError("--min-level must be a number from 0 to 1.");
                    break;
                case "--motor-rom-step-test":
                    _protocolTest = true;
                    break;
                case "--no-cp-step":
                    _disableCapturePointStepping = true;
                    break;
                case "--levels":
                    if (i + 1 >= a.Length)
                    {
                        GD.PushError("--levels requires comma-separated values from 0 to 1.");
                        _probeInvalid = true;
                        break;
                    }
                    var levels = new List<float>();
                    bool validLevels = true;
                    foreach (string text in a[++i].Split(','))
                    {
                        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                            !float.IsFinite(value) || value < 0f || value > 1f)
                        {
                            validLevels = false;
                            break;
                        }
                        levels.Add(value);
                    }
                    if (validLevels && levels.Count > 0)
                        _protocolLevels = levels.ToArray();
                    else
                    {
                        GD.PushError("--levels requires comma-separated values from 0 to 1.");
                        _probeInvalid = true;
                    }
                    break;
                                case "--allow-swing-leg-reflex":
                    _allowSwingLegReflex = true;
                    break;
                case "--hip-upper":
                    // Only valid with --motor-rom-step-test; validation enforced after all args are parsed.
                    if (i + 1 < a.Length && float.TryParse(a[++i], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float hipUpper) &&
                        float.IsFinite(hipUpper) && hipUpper >= 0.3f && hipUpper <= 1.2f)
                        _hipUpperOverride = hipUpper;
                    else
                    {
                        GD.PushError("--hip-upper must be a number from 0.3 to 1.2.");
                        _probeInvalid = true;
                    }
                    break;
                case "--fresh": _fresh = true; break;
                case "--pristine": UseDamage = false; break;
            }
        }
    }
}
