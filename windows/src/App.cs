using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Snowdrift
{
    /// <summary>The whole app: snow on every monitor, the tray menu, global shortcuts and the music island.</summary>
    internal sealed class SnowApp
    {
        private Renderer renderer;
        private readonly List<Overlay> overlays = new List<Overlay>();
        private List<MonitorInfo> monitors = new List<MonitorInfo>();
        private WF.NotifyIcon tray;
        private WF.ContextMenuStrip menu;
        private IslandWindow island;
        private Action balloonClick;
        private HwndSource hotkeyWindow;
        private readonly Dictionary<int, Action> hotkeys = new Dictionary<int, Action>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double lastTime, lastReassert;
        private bool rebuildPending;
        private DispatcherTimer ticker;
        private SD.Icon iconOn, iconOff;

        private ForegroundInfo fg = new ForegroundInfo();
        private uint frontPid;
        private string frontExe = "", frontName = "", frontPath = "";
        private bool hiddenForFrontApp;
        private long pendingFlakes;
        private bool newYearShowRunning;
        private readonly Dictionary<uint, (string exe, string name, string path)> processes = new Dictionary<uint, (string, string, string)>();

        private static SettingsData D => Settings.S.D;
        private static string L(string ru, string en) => Lang.L(ru, en);

        public void Start()
        {
            try
            {
                renderer = Renderer.Create();
            }
            catch (Exception e)
            {
                Log.Write("Graphics failed: " + e);
                WF.MessageBox.Show(L("Не удалось включить графику Direct3D 11 на этом компьютере.", "Direct3D 11 graphics are not available on this PC."),
                                   "Snowdrift", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error);
                System.Windows.Application.Current.Shutdown();
                return;
            }
            Log.Write($"Started {Version}, graphics: {(renderer.IsWarp ? "WARP (software)" : "GPU")}");

            BuildOverlays();
            SystemEvents.DisplaySettingsChanged += (s, e) => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(ScheduleRebuild));
            Settings.S.Changed += ApplySettings;
            try
            {
                Windows.System.Power.PowerManager.EnergySaverStatusChanged += (s, e) =>
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(ApplySettings));
            }
            catch { }

            SetupTray();
            SetupHotkeys();

            MusicService.Shared.TrackChanged += IslandPuff;
            MusicService.Shared.Start();
            island = new IslandWindow(MusicService.Shared);

            ApplySettings();
            CompositionTarget.Rendering += OnFrame;
            ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            ticker.Tick += (s, e) => Tick();
            ticker.Start();

            if (!D.WelcomeShown)
            {
                Settings.S.Change(d => d.WelcomeShown = true);
                tray.ShowBalloonTip(8000, L("Пусть идёт снег ❄", "Let it snow ❄"),
                    L("Настройки — значок снежинки в трее. Ctrl+Alt+S — включить или выключить снег.",
                      "Settings are behind the snowflake in the tray. Ctrl+Alt+S turns the snow on or off."), WF.ToolTipIcon.None);
            }

            Updater.CleanUpOld();
            if (Program.JustUpdated)
            {
                tray.ShowBalloonTip(6000, L($"Snowdrift обновлён до {Version} ✓", $"Snowdrift updated to {Version} ✓"),
                    L("Всё готово, настройки на месте.", "All set, your settings are kept."), WF.ToolTipIcon.None);
            }
            else if (D.CheckUpdates && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - D.LastUpdateCheck > 6 * 3600)
            {
                var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                first.Tick += (s, e) => { first.Stop(); CheckForUpdates(false); };
                first.Start();
            }

            // 1.1.3 removed the Mac-style touchpad gestures. Whoever switched Windows' own swipes off for them
            // gets one note with a click to the touchpad settings, to switch them back on.
            if (!D.GesturesRemovedNoticeShown && D.GesturesTipShown && WindowsSwipesOff())
            {
                var note = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                note.Tick += (s, e) =>
                {
                    note.Stop();
                    Settings.S.Change(d => d.GesturesRemovedNoticeShown = true);
                    balloonClick = OpenTouchpadSettings;
                    tray.ShowBalloonTip(15000, L("Жесты Snowdrift убраны", "Snowdrift's gestures are gone"),
                        L("Жесты Windows для сенсорной панели сейчас выключены. Нажмите, чтобы открыть настройки и включить их обратно.",
                          "Windows' own touchpad swipes are off. Click to open the settings and switch them back on."), WF.ToolTipIcon.None);
                };
                note.Start();
            }
        }

        private bool stopped;

        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            CompositionTarget.Rendering -= OnFrame;
            ticker?.Stop();
            foreach (var o in overlays) pendingFlakes += o.Sim.TakeLanded();
            if (pendingFlakes > 0)
            {
                D.TotalFlakes += pendingFlakes;
                Settings.S.Save();
            }
            if (tray != null) tray.Visible = false;
            if (hotkeyWindow != null)
                foreach (int id in hotkeys.Keys) Native.UnregisterHotKey(hotkeyWindow.Handle, id);
            foreach (var o in overlays) o.Dispose();
            overlays.Clear();
        }

        private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        // ---- Monitors ----

        private void ScheduleRebuild()
        {
            if (rebuildPending) return;
            rebuildPending = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                rebuildPending = false;
                BuildOverlays();
                ApplySettings();
                island?.Place();
            };
            t.Start();
        }

        private void BuildOverlays()
        {
            foreach (var o in overlays) o.Dispose();
            overlays.Clear();
            monitors = MonitorInfo.All();
            foreach (var m in monitors)
            {
                var o = new Overlay(m, renderer);
                overlays.Add(o);
                Log.Write($"Monitor {m.Bounds} scale {m.Scale:0.##} primary={m.Primary} ok={o.Ok}");
            }
            WindowTracker.Shared.Invalidate();
        }

        // ---- Settings ----

        private static bool EnergySaver()
        {
            try { return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; }
            catch { return false; }
        }

        private void ApplySettings()
        {
            var d = D;
            var config = new SimConfig
            {
                Intensity = d.Intensity, Wind = d.Wind, Drifts = d.Drifts, Tool = d.Tool, Garland = d.Garland,
                Depth = d.Depth, LowPower = EnergySaver(), FlakeSize = d.FlakeSize, Melt = d.Melt, Tree = d.Tree,
                Frost = d.Frost, Sparkle = d.Sparkle,
            };
            foreach (var o in overlays)
            {
                var c = config;
                // The tree stands only on the main screen.
                if (!o.Mon.Primary) c.Tree = false;
                if (o.Sim.Config != c) o.Sim.Config = c;
                o.FullScreenMode = d.FullScreenMode;
                o.Sim.NeedsWindowRefresh = true;
                o.Show(d.Enabled);
            }
            UpdateHidden();
            if (island != null)
            {
                if (d.Island && !island.IsVisible) island.Show();
                else if (!d.Island && island.IsVisible) island.Hide();
            }
            UpdateTrayIcon();
        }

        private void SetEnabled(bool on) => Settings.S.Change(d => { d.PauseUntil = 0; d.Enabled = on; });

        private void ToggleEnabled() => SetEnabled(!D.Enabled);

        /// <summary>Snow only on the desktop: hidden over every app except the allowed ones.</summary>
        private void ToggleDesktopOnly() => Settings.S.Change(d => { d.Enabled = true; d.PauseUntil = 0; d.SnowWhere = d.SnowWhere == 1 ? 0 : 1; });

        private bool AppListed(string exe) => D.SnowWhere == 1 ? D.ShownApps.ContainsKey(exe) : D.HiddenApps.ContainsKey(exe);

        private void ToggleApp(string exe, string name)
        {
            Settings.S.Change(d =>
            {
                var list = d.SnowWhere == 1 ? d.ShownApps : d.HiddenApps;
                if (list.ContainsKey(exe)) list.Remove(exe); else list[exe] = name;
            });
        }

        private void ToggleFrontApp()
        {
            if (frontExe.Length == 0 || frontExe == "explorer.exe") return;
            ToggleApp(frontExe, frontName);
        }

        private void Shovel()
        {
            foreach (var o in overlays) o.Sim.ClearSnow();
        }

        private void Fireworks()
        {
            if (!D.Enabled) SetEnabled(true);
            foreach (var o in overlays) o.Sim.Festive.LaunchFireworks(25);
        }

        /// <summary>New track: the island breathes a puff of snow onto the screen below it.</summary>
        private void IslandPuff()
        {
            if (!D.IslandPuff || !D.Island || !D.Enabled || overlays.Count == 0 || island == null) return;
            var o = overlays.Find(x => x.Mon.Primary) ?? overlays[0];
            o.Sim.Puff(o.Sim.W / 2, (float)island.View.Bottom + 6, 90);
        }

        // ---- The app in front ----

        private (string exe, string name, string path) Resolve(uint pid)
        {
            if (processes.TryGetValue(pid, out var r)) return r;
            string path = Native.ProcessPath(pid) ?? "";
            string exe = path.Length > 0 ? Path.GetFileName(path).ToLowerInvariant() : "";
            string name = "";
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                name = string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.ProductName : vi.FileDescription;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(name)) name = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : "";
            name = name.Trim();
            r = (exe, name, path);
            if (processes.Count > 500) processes.Clear();
            processes[pid] = r;
            return r;
        }

        private void UpdateFrontApp()
        {
            if (fg.Hwnd == IntPtr.Zero || fg.IsOwn) return;
            string cls = Native.ClassName(fg.Hwnd);
            // The taskbar, tray and Start menu don't count: clicking them keeps the app that was in front.
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "NotifyIconOverflowWindow" ||
                cls == "TopLevelWindowForOverflowXamlIsland" || cls == "XamlExplorerHostIslandWindow" || cls == "Windows.UI.Core.CoreWindow")
                return;
            if (fg.Pid == frontPid && !(cls == "Progman" || cls == "WorkerW")) return;
            if (cls == "Progman" || cls == "WorkerW")
            {
                frontPid = 0;
                frontExe = "";
                frontName = "";
                frontPath = "";
            }
            else
            {
                frontPid = fg.Pid;
                var r = Resolve(fg.Pid);
                frontExe = r.exe;
                frontName = r.name;
                frontPath = r.path;
            }
            UpdateHidden();
        }

        private void UpdateHidden()
        {
            string id = frontExe;
            bool desktop = id.Length == 0 || id == "explorer.exe";
            hiddenForFrontApp = D.SnowWhere == 1 ? !(desktop || D.ShownApps.ContainsKey(id)) : !desktop && D.HiddenApps.ContainsKey(id);
            UpdateTrayIcon();
        }

        // ---- Every frame ----

        private void OnFrame(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)(now - lastTime);
            lastTime = now;
            if (dt <= 0 || dt > 0.1f) dt = 1f / 60;

            fg = ForegroundInfo.Read(now);
            UpdateFrontApp();

            if (D.Enabled && overlays.Count > 0)
            {
                var snap = WindowTracker.Shared.Poll(now, fg, monitors);
                Native.GetCursorPos(out var cursor);
                foreach (var o in overlays) o.Frame(now, dt, snap, fg, hiddenForFrontApp, cursor);
            }
            if (island != null && D.Island)
            {
                bool full = false;
                foreach (var m in monitors) if (m.Primary && fg.FullScreenOn(m)) full = true;
                island.Tick(dt, full);
            }
            if (now - lastReassert > 2)
            {
                lastReassert = now;
                foreach (var o in overlays) o.Reassert();
                island?.Reassert();
            }
            LiveDebug.Tick(now, overlays, fg, hiddenForFrontApp);
        }

        // ---- Updates ----

        private bool checkingUpdates;
        /// <summary>A new version already sits in place of Snowdrift.exe; it starts with the next restart.</summary>
        private Version readyVersion;

        /// <summary>Asks GitHub for a newer version. The menu button (manual) goes straight on to update;
        /// the daily check installs it quietly when automatic updates are on, or offers it once otherwise.</summary>
        private async void CheckForUpdates(bool manual)
        {
            var u = Updater.Shared;
            if (checkingUpdates || u.Busy) return;
            if (manual && readyVersion != null) { RestartToUpdate(); return; }
            checkingUpdates = true;
            bool read;
            try { read = await u.CheckAsync(); }
            finally { checkingUpdates = false; }
            D.LastUpdateCheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Settings.S.Save();
            UpdateTrayIcon();
            if (readyVersion != null && u.Latest != null && u.Latest <= readyVersion) return;
            if (u.Available)
            {
                if (manual) UpdateNow();
                else if (D.AutoUpdate) InstallQuietly();
                else Announce();
            }
            else if (manual)
            {
                tray.ShowBalloonTip(5000, "Snowdrift",
                    read ? L($"У вас последняя версия — {Version} ✓", $"You have the latest version — {Version} ✓")
                         : L("Не получилось связаться с GitHub. Проверьте интернет и попробуйте ещё раз.",
                             "Couldn't reach GitHub. Check the internet connection and try again."), WF.ToolTipIcon.None);
            }
        }

        /// <summary>Once per version: a note that an update is waiting, with a click to install it.</summary>
        private void Announce()
        {
            string v = Updater.Shared.Latest.ToString();
            if (D.NotifiedVersion == v) return;
            D.NotifiedVersion = v;
            Settings.S.Save();
            balloonClick = UpdateNow;
            tray.ShowBalloonTip(15000, L($"Вышел Snowdrift {v} ❄", $"Snowdrift {v} is out ❄"),
                L("Нажмите, чтобы обновиться — займёт несколько секунд, настройки сохранятся.",
                  "Click to update — it takes a few seconds and keeps your settings."), WF.ToolTipIcon.None);
        }

        /// <summary>Automatic update: download and put the new version in place now, restart when nobody is using the computer.</summary>
        private async void InstallQuietly()
        {
            var u = Updater.Shared;
            string error = await u.InstallAsync();
            if (error != null)
            {
                Log.Write("Automatic update failed: " + error);
                Announce();
                return;
            }
            readyVersion = u.Latest;
            UpdateTrayIcon();
            Log.Write($"Update {readyVersion} is in place; Snowdrift restarts into it once the computer is idle");
        }

        /// <summary>The menu button: update right now and restart.</summary>
        private async void UpdateNow()
        {
            var u = Updater.Shared;
            if (u.Busy) return;
            if (readyVersion != null) { RestartToUpdate(); return; }
            tray.Text = L("Snowdrift — обновляется…", "Snowdrift — updating…");
            tray.ShowBalloonTip(4000, L("Обновляю Snowdrift…", "Updating Snowdrift…"),
                L("Скачиваю новую версию с GitHub, это займёт несколько секунд.", "Downloading the new version from GitHub, a few seconds."), WF.ToolTipIcon.None);
            string error = await u.InstallAsync();
            if (error == null)
            {
                readyVersion = u.Latest;
                RestartToUpdate();
                return;
            }
            UpdateTrayIcon();
            var answer = WF.MessageBox.Show(error + L("\n\nОткрыть страницу загрузки?", "\n\nOpen the download page?"),
                                            "Snowdrift", WF.MessageBoxButtons.YesNo, WF.MessageBoxIcon.Warning);
            if (answer == WF.DialogResult.Yes) Updater.OpenPage(u.PageUrl);
        }

        private void RestartToUpdate()
        {
            try
            {
                Updater.Relaunch();
                Quit();
            }
            catch (Exception e)
            {
                Log.Write("Restart after update failed: " + e.Message);
            }
        }

        /// <summary>The always-visible first item of the menu.</summary>
        private WF.ToolStripMenuItem UpdateButton()
        {
            var u = Updater.Shared;
            WF.ToolStripMenuItem item;
            if (readyVersion != null)
                item = Item(L($"⬆ Перезапустить в версию {readyVersion}", $"⬆ Restart into version {readyVersion}"), RestartToUpdate);
            else if (u.Busy)
                return Info(L("⬆ Обновляю…", "⬆ Updating…"));
            else if (checkingUpdates)
                return Info(L("⬆ Проверяю обновления…", "⬆ Checking for updates…"));
            else if (u.Available)
                item = Item(L($"⬆ Обновить до {u.Latest}", $"⬆ Update to {u.Latest}"), UpdateNow);
            else
                return Item(L("⬆ Обновить Snowdrift", "⬆ Update Snowdrift"), () => CheckForUpdates(true));
            item.Font = new SD.Font(item.Font, SD.FontStyle.Bold);
            return item;
        }

        /// <summary>Once a second: timed pause, snowflake counter, New Year fireworks, the daily update check.</summary>
        private void Tick()
        {
            if (D.CheckUpdates && !checkingUpdates && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - D.LastUpdateCheck > 24 * 3600)
                CheckForUpdates(false);
            // A downloaded update starts once nobody has touched the computer for three minutes
            // (and no full-screen app or game is running), so it never interrupts anything.
            if (readyVersion != null && Updater.IdleSeconds() > 180 && !fg.SystemBusy)
            {
                Log.Write($"Restarting into {readyVersion} while the computer is idle");
                RestartToUpdate();
                return;
            }

            double until = D.PauseUntil;
            if (until > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= until) SetEnabled(true);

            foreach (var o in overlays) pendingFlakes += o.Sim.TakeLanded();
            if (pendingFlakes > 500)
            {
                D.TotalFlakes += pendingFlakes;
                pendingFlakes = 0;
                Settings.S.Save();
            }

            // Fireworks from 23:59:50 on December 31 until 00:10 on January 1.
            if (!D.NewYearFireworks) return;
            var t = DateTime.Now;
            bool lastSeconds = t.Month == 12 && t.Day == 31 && t.Hour == 23 && t.Minute == 59 && t.Second >= 50;
            bool firstMinutes = t.Month == 1 && t.Day == 1 && t.Hour == 0 && t.Minute < 10;
            if ((lastSeconds || firstMinutes) && D.Enabled)
            {
                if (!newYearShowRunning)
                {
                    newYearShowRunning = true;
                    float left = firstMinutes ? 600 - (t.Minute * 60 + t.Second) : 610;
                    foreach (var o in overlays) o.Sim.Festive.LaunchFireworks(Math.Max(30, left + 10));
                }
            }
            else newYearShowRunning = false;
        }

        // ---- Shortcuts ----

        private void SetupHotkeys()
        {
            var p = new HwndSourceParameters("SnowdriftHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = Native.HWND_MESSAGE };
            hotkeyWindow = new HwndSource(p);
            hotkeyWindow.AddHook(HotkeyHook);
            var keys = new (char key, Action action)[]
            {
                ('S', ToggleEnabled), ('A', ToggleDesktopOnly), ('H', ToggleFrontApp), ('D', Shovel), ('F', Fireworks),
            };
            int id = 1;
            foreach (var (key, action) in keys)
            {
                if (Native.RegisterHotKey(hotkeyWindow.Handle, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, key))
                    hotkeys[id] = action;
                else
                    Log.Write($"Shortcut Ctrl+Alt+{key} is taken by another app");
                id++;
            }
        }

        private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)Native.WM_HOTKEY && hotkeys.TryGetValue(wParam.ToInt32(), out var action))
            {
                action();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---- Tray and menu ----

        private static SD.Icon MakeTrayIcon(bool dim)
        {
            int size = Math.Max(16, WF.SystemInformation.SmallIconSize.Width * 2);
            using var bmp = Atlas.MakeIcon(size, dim);
            return SD.Icon.FromHandle(bmp.GetHicon());
        }

        private void SetupTray()
        {
            iconOn = MakeTrayIcon(false);
            iconOff = MakeTrayIcon(true);
            menu = new WF.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = true };
            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            tray = new WF.NotifyIcon { Icon = iconOn, Text = "Snowdrift", Visible = true, ContextMenuStrip = menu };
            tray.BalloonTipClicked += (s, e) => { var a = balloonClick; balloonClick = null; a?.Invoke(); };
            tray.BalloonTipClosed += (s, e) => balloonClick = null;
            tray.MouseUp += (s, e) =>
            {
                if (e.Button != WF.MouseButtons.Left) return;
                typeof(WF.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);
            };
        }

        private void UpdateTrayIcon()
        {
            if (tray == null) return;
            var icon = D.Enabled && !hiddenForFrontApp ? iconOn : iconOff;
            if (tray.Icon != icon) tray.Icon = icon;
            tray.Text = readyVersion != null ? L($"Snowdrift — версия {readyVersion} готова", $"Snowdrift — {readyVersion} is ready")
                      : Updater.Shared.Available ? L($"Snowdrift — есть версия {Updater.Shared.Latest}", $"Snowdrift — {Updater.Shared.Latest} is available")
                      : D.Enabled ? "Snowdrift" : L("Snowdrift — пауза", "Snowdrift — paused");
        }

        private static WF.ToolStripMenuItem Item(string text, Action action, bool check = false, string shortcut = null, SD.Image image = null)
        {
            var item = new WF.ToolStripMenuItem(text) { Checked = check, Image = image };
            if (shortcut != null) item.ShortcutKeyDisplayString = shortcut;
            if (action != null) item.Click += (s, e) => action();
            return item;
        }

        private static WF.ToolStripMenuItem Info(string text)
        {
            return new WF.ToolStripMenuItem(text) { Enabled = false };
        }

        private static WF.ToolStripMenuItem Choice(string title, string[] options, int selected, Action<int> set)
        {
            var item = new WF.ToolStripMenuItem(title);
            for (int i = 0; i < options.Length; i++)
            {
                int k = i;
                item.DropDownItems.Add(Item(options[i], () => set(k), i == selected));
            }
            return item;
        }

        private static void Change(Action<SettingsData> f) => Settings.S.Change(f);

        private void BuildMenu()
        {
            var m = menu.Items;
            m.Clear();
            var d = D;

            var head = Info("Snowdrift");
            head.Font = new SD.Font(head.Font, SD.FontStyle.Bold);
            m.Add(head);
            m.Add(UpdateButton());
            m.Add(Info(NewYearCountdown()));
            long landed = d.TotalFlakes + pendingFlakes;
            foreach (var o in overlays) landed += o.Sim.PeekLanded;
            if (landed > 0) m.Add(Info(L("❄ Улеглось снежинок: ", "❄ Snowflakes settled: ") + landed.ToString("N0")));
            m.Add(new WF.ToolStripSeparator());

            m.Add(Item(L("Снег включён", "Snow on"), ToggleEnabled, d.Enabled, "Ctrl+Alt+S"));
            if (d.Enabled)
            {
                m.Add(Item(L("Пауза на 1 час", "Pause for 1 hour"), () => Change(x =>
                {
                    x.Enabled = false;
                    x.PauseUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
                })));
            }
            else if (d.PauseUntil > 0)
            {
                var back = DateTimeOffset.FromUnixTimeSeconds((long)d.PauseUntil).ToLocalTime();
                m.Add(Info(L("Снег вернётся в ", "Snow returns at ") + back.ToString("t")));
            }
            m.Add(new WF.ToolStripSeparator());

            // Where the snow shows.
            bool desktopOnly = d.SnowWhere == 1;
            m.Add(Item(L("Прятать снег над всеми приложениями", "Hide snow over all apps"), ToggleDesktopOnly, desktopOnly, "Ctrl+Alt+A"));
            if (desktopOnly) m.Add(Info(L("   Снег идёт только на рабочем столе", "   Snow falls only on the desktop")));
            if (frontExe.Length > 0 && frontExe != "explorer.exe")
            {
                string exe = frontExe, name = frontName;
                string title = desktopOnly ? L($"Показывать снег над «{name}»", $"Show snow over “{name}”")
                                           : L($"Прятать снег над «{name}»", $"Hide snow over “{name}”");
                m.Add(Item(title, () => ToggleApp(exe, name), AppListed(exe), "Ctrl+Alt+H", AppIcon(frontPath)));
            }
            m.Add(AppsSubmenu(desktopOnly));
            m.Add(Choice(L("Полноэкранный режим", "Full screen"),
                         new[] { L("Снег остаётся", "Snow stays"), L("Снег прячется", "Snow hides"),
                                 L("Прячется и над развёрнутыми окнами", "Hides over maximized windows too") },
                         d.FullScreenMode, v => Change(x => x.FullScreenMode = v)));
            m.Add(new WF.ToolStripSeparator());

            m.Add(Choice(L("Снегопад", "Snowfall"),
                         new[] { L("Лёгкий снег", "Light snow"), L("Ровный снегопад", "Steady snowfall"), L("Метель", "Blizzard") },
                         d.Intensity, v => Change(x => x.Intensity = v)));
            m.Add(Choice(L("Снежинки", "Snowflakes"), new[] { L("Мелкие", "Small"), L("Обычные", "Normal"), L("Крупные", "Large") },
                         d.FlakeSize, v => Change(x => x.FlakeSize = v)));
            m.Add(Choice(L("Ветер", "Wind"),
                         new[] { L("← Сильный", "← Strong"), L("← Слабый", "← Light"), L("Штиль", "Calm"), L("Слабый →", "Light →"), L("Сильный →", "Strong →") },
                         d.Wind + 2, v => Change(x => x.Wind = v - 2)));
            m.Add(Choice(L("Сугробы", "Drifts"),
                         new[] { L("Без сугробов", "No drifts"), L("Низкие", "Low"), L("Средние", "Medium"), L("Высокие", "High") },
                         d.Drifts, v => Change(x => x.Drifts = v)));
            m.Add(Choice(L("Курсор", "Cursor"),
                         new[] { L("Варежка — сметает снег", "Mitten — sweeps snow"), L("Снежки — катает шары", "Snowballs — rolls balls"),
                                 L("Не трогает снег", "Leaves snow alone") },
                         d.Tool, v => Change(x => x.Tool = v)));
            m.Add(new WF.ToolStripSeparator());

            var islandMenu = new WF.ToolStripMenuItem(L("Снежный остров 🎵", "Snow island 🎵"));
            islandMenu.DropDownItems.Add(Info(L("Музыка вверху экрана: наведите курсор", "Music at the top of the screen: hover over it")));
            islandMenu.DropDownItems.Add(Item(L("Показывать остров с музыкой", "Show the music island"), () => Change(x => x.Island = !x.Island), d.Island));
            islandMenu.DropDownItems.Add(Item(L("Снежный выдох при смене трека", "Snow puff on a new track"), () => Change(x => x.IslandPuff = !x.IslandPuff), d.IslandPuff));
            m.Add(islandMenu);

            var festive = new WF.ToolStripMenuItem(L("Новогоднее 🎄", "Holiday 🎄"));
            festive.DropDownItems.Add(Item(L("Запустить салют 🎆", "Launch fireworks 🎆"), Fireworks, false, "Ctrl+Alt+F"));
            festive.DropDownItems.Add(Item(L("Салют в новогоднюю полночь", "Fireworks at New Year's midnight"), () => Change(x => x.NewYearFireworks = !x.NewYearFireworks), d.NewYearFireworks));
            festive.DropDownItems.Add(new WF.ToolStripSeparator());
            festive.DropDownItems.Add(Item(L("Ёлка в углу экрана", "Christmas tree in the corner"), () => Change(x => x.Tree = !x.Tree), d.Tree));
            festive.DropDownItems.Add(Choice(L("Гирлянда", "Garland"),
                new[] { L("Выключена", "Off"), L("Горит", "Steady"), L("Мерцает", "Twinkle"), L("Бегущие огни", "Chasing") },
                d.Garland, v => Change(x => x.Garland = v)));
            festive.DropDownItems.Add(Item(L("Иней по углам экрана", "Frost in the screen corners"), () => Change(x => x.Frost = !x.Frost), d.Frost));
            festive.DropDownItems.Add(Item(L("Искрящийся снег", "Sparkling snow"), () => Change(x => x.Sparkle = !x.Sparkle), d.Sparkle));
            m.Add(festive);
            m.Add(Item(L("Оттепель — снег понемногу тает", "Thaw — snow slowly melts"), () => Change(x => x.Melt = !x.Melt), d.Melt));
            m.Add(Item(L("Дальний снег за окнами", "Distant snow behind windows"), () => Change(x => x.Depth = !x.Depth), d.Depth));
            m.Add(new WF.ToolStripSeparator());

            m.Add(Item(L("Убрать весь снег", "Shovel all snow"), Shovel, false, "Ctrl+Alt+D"));
            bool anyBalls = false;
            foreach (var o in overlays) if (o.Sim.HasBalls) anyBalls = true;
            if (anyBalls)
                m.Add(Item(L("Убрать снежки и снеговиков", "Remove snowballs and snowmen"), () => { foreach (var o in overlays) o.Sim.ClearBalls(); }));
            m.Add(Item(L("Как слепить снеговика…", "How to build a snowman…"), ShowHelp));
            m.Add(new WF.ToolStripSeparator());

            m.Add(Item(L("Запускать вместе с Windows", "Start with Windows"), () => SetAutoStart(!AutoStart), AutoStart));
            var updates = new WF.ToolStripMenuItem(L("Обновления", "Updates"));
            updates.DropDownItems.Add(Info(L($"У вас версия {Version}", $"You have version {Version}")));
            updates.DropDownItems.Add(Item(L("Обновлять автоматически", "Update automatically"), () => Change(x => x.AutoUpdate = !x.AutoUpdate), d.AutoUpdate));
            updates.DropDownItems.Add(Info(L("   скачивает сам, перезапускается, пока вы не за компьютером",
                                             "   downloads on its own, restarts while you're away")));
            updates.DropDownItems.Add(Item(L("Проверять раз в день", "Check once a day"), () => Change(x => x.CheckUpdates = !x.CheckUpdates), d.CheckUpdates));
            updates.DropDownItems.Add(Item(L("Что нового — страница на GitHub", "What's new — the GitHub page"), () => Updater.OpenPage(Updater.Shared.PageUrl)));
            m.Add(updates);
            m.Add(Item(L("О Snowdrift", "About Snowdrift"), ShowAbout));
            m.Add(Item(L("Выйти", "Quit"), Quit));
        }

        /// <summary>Whether Windows' own three- or four-finger swipes are set to "Nothing".</summary>
        private static bool WindowsSwipesOff()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
                return k?.GetValue("ThreeFingerSlideEnabled") is int three && three == 0 || k?.GetValue("FourFingerSlideEnabled") is int four && four == 0;
            }
            catch { return false; }
        }

        private static void OpenTouchpadSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true }); }
            catch (Exception e) { Log.Write("Touchpad settings: " + e.Message); }
        }

        private static SD.Image AppIcon(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using var icon = SD.Icon.ExtractAssociatedIcon(path);
                return icon == null ? null : new SD.Bitmap(icon.ToBitmap(), 16, 16);
            }
            catch { return null; }
        }

        /// <summary>Every app with a window open (plus the apps already in the list), each with a checkmark.</summary>
        private WF.ToolStripMenuItem AppsSubmenu(bool desktopOnly)
        {
            var list = desktopOnly ? D.ShownApps : D.HiddenApps;
            string title = desktopOnly ? L("Показывать снег над приложениями", "Show snow over apps")
                                       : L("Прятать снег над приложениями", "Hide snow over apps");
            if (list.Count > 0) title += $" ({list.Count})";
            var item = new WF.ToolStripMenuItem(title);
            item.DropDownItems.Add(Info(desktopOnly ? L("Отметьте, где снег всё равно нужен:", "Tick the apps that still get snow:")
                                                    : L("Отметьте, где снег не нужен:", "Tick the apps that get no snow:")));

            var entries = new SortedDictionary<string, (string exe, string path)>(StringComparer.CurrentCultureIgnoreCase);
            var seen = new HashSet<string>();
            uint own = (uint)Environment.ProcessId;
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.IsCloaked(h)) return true;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                var r = Native.FrameBounds(h);
                if (r.Width < 120 || r.Height < 60) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == own) return true;
                var p = Resolve(pid);
                if (p.exe.Length == 0 || p.exe == "explorer.exe" || !seen.Add(p.exe)) return true;
                string key = p.name.Length > 0 ? p.name : p.exe;
                if (!entries.ContainsKey(key)) entries[key] = (p.exe, p.path);
                return true;
            }, IntPtr.Zero);
            foreach (var kv in list)
            {
                if (seen.Contains(kv.Key)) continue;
                string key = kv.Value.Length > 0 ? kv.Value : kv.Key;
                if (!entries.ContainsKey(key)) entries[key] = (kv.Key, "");
            }
            foreach (var kv in entries)
            {
                string exe = kv.Value.exe, name = kv.Key;
                item.DropDownItems.Add(Item(name, () => ToggleApp(exe, name), list.ContainsKey(exe), null, AppIcon(kv.Value.path)));
            }
            if (list.Count > 0)
            {
                item.DropDownItems.Add(new WF.ToolStripSeparator());
                item.DropDownItems.Add(Item(L("Снять все отметки", "Clear all"), () => Change(x =>
                {
                    if (desktopOnly) x.ShownApps.Clear(); else x.HiddenApps.Clear();
                })));
            }
            return item;
        }

        private static string NewYearCountdown()
        {
            var today = DateTime.Today;
            if (today.Month == 1 && today.Day <= 7) return L("🎄 С Новым годом!", "🎄 Happy New Year!");
            int days = (int)(new DateTime(today.Year + 1, 1, 1) - today).TotalDays;
            if (!Lang.IsRu) return $"🎄 {days} day{(days == 1 ? "" : "s")} until New Year";
            int d10 = days % 10, d100 = days % 100;
            string word = d10 == 1 && d100 != 11 ? "день" : (d10 >= 2 && d10 <= 4 && !(d100 >= 12 && d100 <= 14) ? "дня" : "дней");
            return $"🎄 До Нового года {days} {word}";
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static bool AutoStart
        {
            get
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    var v = k?.GetValue("Snowdrift") as string;
                    return v != null && Environment.ProcessPath != null && v.Contains(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        private static void SetAutoStart(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue("Snowdrift", $"\"{Environment.ProcessPath}\"");
                else k.DeleteValue("Snowdrift", false);
            }
            catch (Exception e)
            {
                Log.Write("Autostart failed: " + e.Message);
            }
        }

        private static void ShowHelp()
        {
            string text = L(
@"Снег ложится на окна, панель задач и низ экрана. Сдвиньте окно — сугроб поедет вместе с ним, резко дёрните — снег слетит.

Варежка: проведите курсором по сугробу, и снег разлетится.

Снеговик: в меню (значок снежинки в трее) выберите «Курсор → Снежки» и поводите курсором по сугробу, пока не скатается снежок. Толкайте его курсором — он катится и растёт. Возьмите снежок мышкой и положите на другой, побольше. Два шара — уже снеговик, три — снеговик с ведром или цилиндром.

Быстрые клавиши работают в любом приложении:
Ctrl+Alt+S — включить или выключить снег
Ctrl+Alt+A — прятать снег над всеми приложениями (снег только на рабочем столе)
Ctrl+Alt+H — спрятать или вернуть снег над текущим приложением
Ctrl+Alt+D — убрать весь снег
Ctrl+Alt+F — салют

Снежный остров 🎵: когда играет музыка (Spotify, браузер, любой плеер), вверху экрана появляется остров — наведите на него курсор.

Над полноэкранными приложениями снег прячется сам. Клики проходят сквозь снег. Snowdrift не видит содержимое окон — только их границы, а в интернет выходит только чтобы раз в день узнать на GitHub, нет ли новой версии (это можно выключить в меню «Обновления»).",
@"Snow settles on windows, the taskbar and the bottom of the screen. Move a window and its drift rides along; yank it and the snow flies off.

Mitten: sweep the cursor through a drift and the snow scatters.

Snowman: in the menu (the snowflake in the tray) choose Cursor → Snowballs and sweep the cursor through a drift until a snowball forms. Push it to roll it bigger. Pick a snowball up with the mouse and drop it on a bigger one. Two balls make a snowman; three get a bucket or a top hat.

Shortcuts work in any app:
Ctrl+Alt+S — snow on or off
Ctrl+Alt+A — hide snow over all apps (snow only on the desktop)
Ctrl+Alt+H — hide or bring back snow over the current app
Ctrl+Alt+D — shovel all snow
Ctrl+Alt+F — fireworks

Snow island 🎵: while music plays (Spotify, a browser, any player) an island appears at the top of the screen — hover over it.

Snow hides over full-screen apps on its own. Clicks go through the snow. Snowdrift never sees what is inside your windows, only where they are, and goes online only to ask GitHub once a day whether there is a new version (switch it off under Updates).");
            WF.MessageBox.Show(text, L("Пусть идёт снег ❄", "Let it snow ❄"), WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
        }

        private static void ShowAbout()
        {
            WF.MessageBox.Show(L(
                $"Snowdrift {Version}\n\nСнег на рабочем столе Windows. Бесплатно и с открытым кодом (MIT).\n\nБез аккаунтов, ключей, рекламы и аналитики. В интернет — только проверка обновлений на GitHub (можно выключить). Настройки хранятся только на этом компьютере:\n{Settings.Folder}",
                $"Snowdrift {Version}\n\nSnow on your Windows desktop. Free and open source (MIT).\n\nNo accounts, keys, ads or analytics. Online only to check GitHub for updates (can be switched off). Settings stay on this PC:\n{Settings.Folder}"),
                "Snowdrift", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Information);
        }

        private void Quit()
        {
            Stop();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
