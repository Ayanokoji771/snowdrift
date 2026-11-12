using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Snowdrift
{
    /// <summary>Updates from the project's GitHub releases. The only thing sent is an ordinary request for
    /// the latest release; the download is checked against the release's SHA-256 checksums before it
    /// replaces Snowdrift.exe. Settings stay where they are.</summary>
    internal sealed class Updater
    {
        public const string Repo = "Ayanokoji771/snowdrift";
        public static readonly Updater Shared = new Updater();

        private static readonly HttpClient Http = CreateClient();

        public Version Latest { get; private set; }
        public string PageUrl { get; private set; } = $"https://github.com/{Repo}/releases/latest";
        public bool Busy { get; private set; }
        private string zipUrl, sumsUrl;

        public static Version Current
        {
            get
            {
                string pretend = Environment.GetEnvironmentVariable("SNOWDRIFT_PRETEND_VERSION");
                if (pretend != null && Version.TryParse(pretend, out var p)) return p;
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public bool Available => Latest != null && Latest > Current && zipUrl != null && sumsUrl != null;

        public static string AssetName =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Snowdrift-Windows-ARM64.zip" : "Snowdrift-Windows-x64.zip";

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Snowdrift-Updater");
            return c;
        }

        /// <summary>Asks GitHub for the latest release. True when it was read (newer or not).
        /// Uses the release page's redirect rather than the GitHub API: the API allows only 60 requests an hour
        /// per address, and many people share one (offices, mobile networks).</summary>
        public async Task<bool> CheckAsync()
        {
            try
            {
                using var page = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{Repo}/releases/latest");
                using var r = await Http.SendAsync(page).ConfigureAwait(false);
                r.EnsureSuccessStatusCode();
                string url = r.RequestMessage?.RequestUri?.ToString() ?? "";
                const string marker = "/releases/tag/";
                int i = url.IndexOf(marker, StringComparison.Ordinal);
                if (i < 0) return false;
                string tag = Uri.UnescapeDataString(url.Substring(i + marker.Length).Trim('/'));
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return false;
                string files = $"https://github.com/{Repo}/releases/download/{tag}/";
                // Releases without checksums (1.0.0) can't be installed automatically.
                using var sums = new HttpRequestMessage(HttpMethod.Head, files + "SHA256SUMS.txt");
                using var sr = await Http.SendAsync(sums).ConfigureAwait(false);
                sumsUrl = sr.IsSuccessStatusCode ? files + "SHA256SUMS.txt" : null;
                zipUrl = files + AssetName;
                PageUrl = url;
                Latest = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
                return true;
            }
            catch (Exception e)
            {
                Log.Write("Update check failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Downloads, checks and puts the new Snowdrift.exe in place of this one.
        /// Returns null on success, or what went wrong, in words for the person.</summary>
        public async Task<string> InstallAsync(bool force = false)
        {
            if (Busy) return Lang.L("Обновление уже идёт.", "An update is already running.");
            if (!force && !Available) return Lang.L("Новой версии нет.", "There is no newer version.");
            if (zipUrl == null || sumsUrl == null) return Lang.L("В выпуске на GitHub нет нужного файла.", "The GitHub release is missing a file.");
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return "No program path.";
            Busy = true;
            string dir = Path.Combine(Path.GetTempPath(), "Snowdrift-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(dir, AssetName);
                using (var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(zip);
                    await response.Content.CopyToAsync(file).ConfigureAwait(false);
                }
                string sums = await Http.GetStringAsync(sumsUrl).ConfigureAwait(false);
                string expected = ExpectedHash(sums, AssetName);
                string actual = HashOf(zip);
                if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write($"Update: checksum mismatch for {AssetName}: expected {expected}, got {actual}");
                    return Lang.L("Скачанный файл не прошёл проверку контрольной суммы. Ничего не изменено.",
                                  "The download failed its checksum test. Nothing was changed.");
                }

                string unpacked = Path.Combine(dir, "new");
                ZipFile.ExtractToDirectory(zip, unpacked);
                string fresh = Path.Combine(unpacked, "Snowdrift.exe");
                if (!File.Exists(fresh)) return Lang.L("В архиве нет Snowdrift.exe.", "The archive has no Snowdrift.exe.");

                // A running program can't be overwritten, but it can be renamed out of the way.
                string old = exe + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(exe, old);
                try
                {
                    File.Copy(fresh, exe);
                }
                catch
                {
                    File.Move(old, exe);
                    throw;
                }
                Log.Write($"Updated {Current} → {Latest} ({actual})");
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return Lang.L("Нет прав на запись в папку с Snowdrift. Скачайте новую версию со страницы выпуска.",
                              "Snowdrift's folder is not writable. Download the new version from the release page.");
            }
            catch (Exception e)
            {
                Log.Write("Update failed: " + e);
                return Lang.L("Не удалось обновиться: ", "Update failed: ") + e.Message;
            }
            finally
            {
                Busy = false;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Starts the new Snowdrift.exe; it waits for this one to quit.</summary>
        public static void Relaunch()
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--updated") { UseShellExecute = false });
        }

        /// <summary>The old program file is locked until the previous copy has quit; tidy it up afterwards.</summary>
        public static void CleanUpOld()
        {
            string old = Environment.ProcessPath + ".old";
            if (!File.Exists(old)) return;
            Task.Run(async () =>
            {
                for (int i = 0; i < 30 && File.Exists(old); i++)
                {
                    try { File.Delete(old); } catch { await Task.Delay(1000); }
                }
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        /// <summary>Seconds since the last key press or mouse move.</summary>
        public static double IdleSeconds()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
        }

        public static void OpenPage(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Log.Write("Open page: " + e.Message); }
        }

        private static string ExpectedHash(string sums, string name)
        {
            foreach (string raw in sums.Split('\n'))
            {
                string line = raw.Trim();
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string file = line.Substring(space).Trim().TrimStart('*');
                if (file == name) return line.Substring(0, space);
            }
            return null;
        }

        private static string HashOf(string path)
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }

        /// <summary>--update-test out.txt: the whole update on a copy of the program, without restarting it.</summary>
        public static int RunTest(string outPath)
        {
            var sb = new StringBuilder();
            bool ok = false;
            try
            {
                var u = Shared;
                string exe = Environment.ProcessPath;
                string before = HashOf(exe);
                sb.AppendLine($"this copy: {Current}, {AssetName}");
                bool read = u.CheckAsync().GetAwaiter().GetResult();
                sb.AppendLine($"latest on GitHub: {(read ? u.Latest?.ToString() : "unreadable")}, newer: {u.Available}, page {u.PageUrl}");
                if (read)
                {
                    string error = u.InstallAsync(force: true).GetAwaiter().GetResult();
                    string after = HashOf(exe);
                    var info = FileVersionInfo.GetVersionInfo(exe);
                    sb.AppendLine($"install: {error ?? "ok"}; program file {(before == after ? "unchanged" : "replaced")}, now version {info.ProductVersion}, old copy kept: {File.Exists(exe + ".old")}");
                    ok = error == null && before != after;
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("exception: " + e);
            }
            sb.AppendLine(ok ? "PASS update" : "FAIL update");
            File.WriteAllText(outPath, sb.ToString());
            return ok ? 0 : 1;
        }
    }
}
