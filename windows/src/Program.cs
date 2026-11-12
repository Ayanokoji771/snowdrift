using System;
using System.IO;
using System.Threading;
using System.Windows;
using WF = System.Windows.Forms;

namespace Snowdrift
{
    internal static class Program
    {
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
                        case "--snapshot": return Snapshot.Run(args[1]) ? 0 : 1;
                        case "--island-preview": return IslandPreview.Run(args[1]) ? 0 : 1;
                        case "--gesture-demo": return GestureDemo.Run(args[1]) ? 0 : 1;
                    }
                }
                catch (Exception e)
                {
                    File.WriteAllText(args[1] + ".error.txt", e.ToString());
                    return 2;
                }
            }

            using var mutex = new Mutex(true, @"Local\Snowdrift.SingleInstance", out bool first);
            if (!first) return 0;

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
