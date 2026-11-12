using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Snowdrift
{
    /// <summary>Russian when Windows is in Russian, English otherwise.</summary>
    internal static class Lang
    {
        public static readonly bool IsRu = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        public static string L(string ru, string en) => IsRu ? ru : en;
    }

    public sealed class SettingsData
    {
        public bool Enabled { get; set; } = true;
        /// <summary>0 light snow, 1 steady snowfall, 2 blizzard.</summary>
        public int Intensity { get; set; } = 1;
        /// <summary>-2 strong left … 0 calm … 2 strong right.</summary>
        public int Wind { get; set; } = 1;
        /// <summary>0 no drifts, 1 low, 2 medium, 3 high.</summary>
        public int Drifts { get; set; } = 2;
        /// <summary>0 mitten, 1 snowballs, 2 cursor leaves snow alone.</summary>
        public int Tool { get; set; } = 0;
        /// <summary>0 off, 1 steady, 2 twinkle, 3 chasing.</summary>
        public int Garland { get; set; } = 2;
        public bool Depth { get; set; } = true;
        /// <summary>0 snow stays over full-screen apps, 1 hides, 2 also hides over maximized windows.</summary>
        public int FullScreenMode { get; set; } = 1;
        public int FlakeSize { get; set; } = 1;
        public bool Melt { get; set; } = false;
        public bool Tree { get; set; } = true;
        public bool Frost { get; set; } = false;
        public bool Sparkle { get; set; } = true;
        public bool NewYearFireworks { get; set; } = true;
        public bool Island { get; set; } = true;
        public bool IslandPuff { get; set; } = true;
        /// <summary>0 snow over every app, 1 snow only on the desktop.</summary>
        public int SnowWhere { get; set; } = 0;
        /// <summary>exe name (lower case) → display name.</summary>
        public Dictionary<string, string> HiddenApps { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ShownApps { get; set; } = new Dictionary<string, string>();
        /// <summary>Unix seconds when a timed pause ends, or 0.</summary>
        public double PauseUntil { get; set; } = 0;
        public long TotalFlakes { get; set; } = 0;
        public bool WelcomeShown { get; set; } = false;
    }

    /// <summary>Settings live in %APPDATA%\Snowdrift\settings.json and never leave this PC.</summary>
    public sealed class Settings
    {
        public static readonly Settings S = new Settings();

        public SettingsData D { get; private set; }
        public event Action Changed;
        private readonly string path;

        private Settings()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snowdrift");
            path = Path.Combine(dir, "settings.json");
            try
            {
                if (File.Exists(path)) D = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path));
            }
            catch { D = null; }
            D ??= new SettingsData();
            Clamp();
        }

        private void Clamp()
        {
            D.Intensity = Math.Clamp(D.Intensity, 0, 2);
            D.Wind = Math.Clamp(D.Wind, -2, 2);
            D.Drifts = Math.Clamp(D.Drifts, 0, 3);
            D.Tool = Math.Clamp(D.Tool, 0, 2);
            D.Garland = Math.Clamp(D.Garland, 0, 3);
            D.FullScreenMode = Math.Clamp(D.FullScreenMode, 0, 2);
            D.FlakeSize = Math.Clamp(D.FlakeSize, 0, 2);
            D.SnowWhere = Math.Clamp(D.SnowWhere, 0, 1);
            D.HiddenApps ??= new Dictionary<string, string>();
            D.ShownApps ??= new Dictionary<string, string>();
        }

        /// <summary>Change settings, save them and tell everyone.</summary>
        public void Change(Action<SettingsData> change)
        {
            change(D);
            Clamp();
            Save();
            Changed?.Invoke();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(D, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snowdrift");
    }

    /// <summary>Small fast random generator for the simulation.</summary>
    public struct Rng
    {
        private ulong s;

        public Rng(ulong seed) { s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; }

        public static Rng Create() => new Rng((ulong)Random.Shared.NextInt64(1, long.MaxValue));

        public ulong Next()
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            return s;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float Unit() => (float)(Next() >> 40) / (float)(1 << 24);

        public float Range(float a, float b) => a + (b - a) * Unit();

        public int Int(int n) => n <= 0 ? 0 : (int)(Next() % (ulong)n);
    }
}
