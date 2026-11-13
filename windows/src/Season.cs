using System;
using System.Collections.Generic;
using System.Globalization;

namespace Snowdrift
{
    /// <summary>The four seasons: snow in winter, cherry petals in spring, dandelion fluff and fireflies in summer,
    /// falling leaves in autumn. 0 winter, 1 spring, 2 summer, 3 autumn.</summary>
    internal static class Seasons
    {
        /// <summary>Countries south of the equator: the seasons there are the other way round.</summary>
        private static readonly HashSet<string> Southern = new HashSet<string>
        {
            "AU", "NZ", "AR", "CL", "UY", "PY", "ZA", "NA", "BW", "LS", "SZ", "MZ", "ZW", "ZM", "MW", "MG", "BR", "BO", "PE",
        };

        /// <summary>The season right now for this PC's region (meteorological: winter is December to February).</summary>
        public static int Now()
        {
            int index = (DateTime.Now.Month % 12) / 3;
            try
            {
                if (Southern.Contains(RegionInfo.CurrentRegion.TwoLetterISORegionName)) index = (index + 2) % 4;
            }
            catch { }
            return index;
        }

        /// <summary>The setting: -1 follows the calendar.</summary>
        public static int Resolve(int setting) => setting >= 0 && setting <= 3 ? setting : Now();

        /// <summary>Evening and night, when fireflies come out.</summary>
        public static bool IsEvening()
        {
            int h = DateTime.Now.Hour;
            return h >= 19 || h < 6;
        }

        public static string Name(int season) => season switch
        {
            1 => Lang.L("Весна 🌸", "Spring 🌸"),
            2 => Lang.L("Лето ☀️", "Summer ☀️"),
            3 => Lang.L("Осень 🍂", "Autumn 🍂"),
            _ => Lang.L("Зима ❄️", "Winter ❄️"),
        };

        /// <summary>The season in effect (kept current by the app; the music island reads it).</summary>
        public static int Current;
    }
}
