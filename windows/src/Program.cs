using System;
using System.IO;
using System.Threading;
using System.Windows;
using WF = System.Windows.Forms;

namespace Snowdrift
{
    internal static class Program
    {
        /// <summary>This copy was just put in place by the updater.</summary>
        public static bool JustUpdated;

        [STAThread]
        private static int Main(string[] args)
        {
            // Test modes used by the build (no window, no tray).
            if (args.Length >= 2 && args[0].StartsWith("--", StringComparison.Ordinal))
            {
                try
                {
                    switch (args[0])
                    {
                        case "--selftest": return SelfTest.Run(args[1]) ? 0 : 1;
                        case "--snapshot":
                        {
                            // --season 0…3 (winter, spring, summer, autumn), --evening for the night sky.
                            int at = Array.IndexOf(args, "--season");
                            int season = at > 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int v) ? v : 0;
                            return Snapshot.Run(args[1], season, Array.IndexOf(args, "--evening") > 0) ? 0 : 1;
                        }
                        case "--island-preview": return IslandPreview.Run(args[1]) ? 0 : 1;
                        case "--update-test": return Updater.RunTest(args[1]);
                    }
                }
                catch (Exception e)
                {
                    File.WriteAllText(args[1] + ".error.txt", e.ToString());
                    return 2;
                }
            }

            // After an update the new copy starts while the old one is still closing: wait for it.
            JustUpdated = Array.IndexOf(args, "--updated") >= 0;
            using var mutex = new Mutex(true, @"Local\Snowdrift.SingleInstance", out bool first);
            if (!first)
            {
                if (!JustUpdated) return 0;
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(20))) return 0;
                }
                catch (AbandonedMutexException) { }
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Crash: " + e.ExceptionObject);
            WF.Application.EnableVisualStyles();
            WF.Application.SetCompatibleTextRenderingDefault(false);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("Error: " + e.Exception);
                e.Handled = true;
            };
            var snow = new SnowApp();
            app.Startup += (s, e) => snow.Start();
            app.Exit += (s, e) => snow.Stop();
            app.Run();
            return 0;
        }
    }
}
