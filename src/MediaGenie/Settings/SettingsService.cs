using System;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MkvPlayer.Settings;

/// <summary>
/// Thin wrapper around the Windows registry so the rest of the app never touches
/// Microsoft.Win32.Registry directly. Per-user settings (volume, last folder, window
/// bounds, equalizer bands, recent playlists) live under HKCU and are always writable.
/// A couple of machine-wide defaults (e.g. a shared media folder for every user on the
/// box) can optionally live under HKLM, but that's only written when the app is running
/// elevated and the user explicitly asks for it from Preferences ("apply for all users").
///
/// Every read checks the key/value actually exists and falls back to a hardcoded default
/// when it doesn't -- a fresh install, or a machine where only HKCU was ever written,
/// must still start up cleanly instead of throwing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SettingsService
{
    private const string UserRootPath = @"Software\MkvPlayer";
    private const string MachineRootPath = @"Software\MkvPlayer";

    // ----- Per-user (HKCU) -----

    public static string GetUserString(string name, string defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserRootPath);
            var value = key?.GetValue(name) as string;
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }
        catch
        {
            return defaultValue;
        }
    }

    public static void SetUserString(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UserRootPath);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch
        {
            // Best-effort: a settings write failing should never crash the app.
        }
    }

    public static double GetUserDouble(string name, double defaultValue)
    {
        var raw = GetUserString(name, string.Empty);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    public static void SetUserDouble(string name, double value)
        => SetUserString(name, value.ToString(CultureInfo.InvariantCulture));

    public static int GetUserInt(string name, int defaultValue)
    {
        var raw = GetUserString(name, string.Empty);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    public static void SetUserInt(string name, int value)
        => SetUserString(name, value.ToString(CultureInfo.InvariantCulture));

    public static bool GetUserBool(string name, bool defaultValue)
    {
        var raw = GetUserString(name, string.Empty);
        return bool.TryParse(raw, out var value) ? value : defaultValue;
    }

    public static void SetUserBool(string name, bool value)
        => SetUserString(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Stores a small array of doubles (e.g. equalizer band gains) as a comma-separated string.</summary>
    public static double[] GetUserDoubleArray(string name, double[] defaultValue)
    {
        var raw = GetUserString(name, string.Empty);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                return defaultValue;
        }
        return parsed;
    }

    public static void SetUserDoubleArray(string name, double[] value)
        => SetUserString(name, string.Join(",", value.Select(v => v.ToString(CultureInfo.InvariantCulture))));

    // ----- Machine-wide (HKLM) — optional, elevation required -----

    public static string? GetMachineString(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MachineRootPath);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to write a machine-wide default. Returns false (without throwing) if the
    /// process isn't elevated or the write otherwise fails -- callers should fall back to
    /// the per-user (HKCU) equivalent in that case rather than surfacing an error.
    /// </summary>
    public static bool TrySetMachineString(string name, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(MachineRootPath);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ----- Well-known setting keys -----

    public static class Keys
    {
        public const string Volume = "Volume";
        public const string LastFolder = "LastFolder";
        public const string DefaultMediaFolder = "DefaultMediaFolder";
        public const string WindowLeft = "WindowLeft";
        public const string WindowTop = "WindowTop";
        public const string WindowWidth = "WindowWidth";
        public const string WindowHeight = "WindowHeight";
        public const string EqualizerEnabled = "EqualizerEnabled";
        public const string EqualizerPreamp = "EqualizerPreamp";
        public const string EqualizerBands = "EqualizerBands";
        public const string LastActiveTab = "LastActiveTab";
        public const string VocalLevel = "VocalLevel";
        public const string TrackSeparationDrums = "TrackSeparationDrums";
        public const string TrackSeparationBass = "TrackSeparationBass";
        public const string TrackSeparationGuitar = "TrackSeparationGuitar";
    }
}
