using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Snowdrift
{
    /// <summary>What is playing right now.</summary>
    public sealed class NowPlaying
    {
        public static readonly Color DefaultAccent = Color.FromRgb(140, 194, 255);

        public string Source = "";
        public string TrackId = "";
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public bool IsPlaying;
        /// <summary>Seconds.</summary>
        public double Duration;
        public double BasePosition;
        public DateTime BaseTime = DateTime.UtcNow;
        public BitmapSource Artwork;
        public Color Accent = DefaultAccent;
        public DateTime LastActive = DateTime.MinValue;
        public bool CanSeek;

        public bool HasTrack => Title.Length > 0;

        public double Position(DateTime now)
        {
            if (Duration <= 0) return 0;
            double p = IsPlaying ? BasePosition + (now - BaseTime).TotalSeconds : BasePosition;
            return Math.Clamp(p, 0, Duration);
        }
    }

    /// <summary>Follows the player Windows shows in its media controls (Spotify, browsers, Apple Music,
    /// Yandex Music…) and controls it. No permission, no internet: the cover comes from the player itself.</summary>
    public sealed class MusicService
    {
        public static readonly MusicService Shared = new MusicService();

        public readonly NowPlaying Now = new NowPlaying();
        /// <summary>A new track started playing (UI thread).</summary>
        public event Action TrackChanged;

        private GlobalSystemMediaTransportControlsSessionManager manager;
        private GlobalSystemMediaTransportControlsSession session;
        private Dispatcher ui;
        private int refreshToken;

        public async void Start()
        {
            ui = Dispatcher.CurrentDispatcher;
            try
            {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                manager.CurrentSessionChanged += (s, e) => ui.BeginInvoke(new Action(Attach));
                manager.SessionsChanged += (s, e) => ui.BeginInvoke(new Action(Attach));
                Attach();
            }
            catch (Exception e)
            {
                Log.Write("Media controls unavailable: " + e.Message);
            }
        }

        private void Attach()
        {
            GlobalSystemMediaTransportControlsSession next = null;
            try { next = manager?.GetCurrentSession(); } catch { }
            if (next != session)
            {
                if (session != null)
                {
                    session.MediaPropertiesChanged -= OnProps;
                    session.PlaybackInfoChanged -= OnPlayback;
                    session.TimelinePropertiesChanged -= OnTimeline;
                }
                session = next;
                if (session != null)
                {
                    session.MediaPropertiesChanged += OnProps;
                    session.PlaybackInfoChanged += OnPlayback;
                    session.TimelinePropertiesChanged += OnTimeline;
                }
            }
            Refresh();
        }

        private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => ui.BeginInvoke(new Action(Refresh));
        private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => ui.BeginInvoke(new Action(Refresh));
        private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => ui.BeginInvoke(new Action(SyncTimeline));

        private void Clear()
        {
            Now.IsPlaying = false;
            Now.Title = "";
            Now.Artist = "";
            Now.Album = "";
            Now.TrackId = "";
            Now.Artwork = null;
            Now.Source = "";
        }

        private async void Refresh()
        {
            var s = session;
            int token = ++refreshToken;
            if (s == null)
            {
                Clear();
                return;
            }
            GlobalSystemMediaTransportControlsSessionMediaProperties props = null;
            try { props = await s.TryGetMediaPropertiesAsync(); } catch { }
            if (token != refreshToken || s != session) return;

            string title = props?.Title ?? "";
            if (title.Length == 0)
            {
                Clear();
                return;
            }
            string artist = props?.Artist ?? "";
            if (artist.Length == 0) artist = props?.AlbumArtist ?? "";
            bool playing = false;
            try
            {
                var info = s.GetPlaybackInfo();
                playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                Now.CanSeek = info?.Controls?.IsPlaybackPositionEnabled ?? false;
            }
            catch { }

            string app = s.SourceAppUserModelId ?? "";
            string id = app + "|" + title + "|" + artist;
            bool changed = id != Now.TrackId;
            Now.Source = SourceName(app);
            Now.Title = title;
            Now.Artist = artist;
            Now.Album = props?.AlbumTitle ?? "";
            Now.IsPlaying = playing;
            Now.LastActive = DateTime.UtcNow;
            SyncTimeline();
            if (changed)
            {
                Now.TrackId = id;
                Now.Artwork = null;
                Now.Accent = NowPlaying.DefaultAccent;
                if (props?.Thumbnail != null) LoadThumbnail(props.Thumbnail, id);
                if (playing) TrackChanged?.Invoke();
            }
        }

        /// <summary>Where in the track we are, extrapolated from the player's last report.</summary>
        public void SyncTimeline()
        {
            var s = session;
            if (s == null) return;
            try
            {
                var tl = s.GetTimelineProperties();
                double duration = (tl.EndTime - tl.StartTime).TotalSeconds;
                if (duration <= 0) duration = tl.EndTime.TotalSeconds;
                double pos = (tl.Position - tl.StartTime).TotalSeconds;
                var updated = tl.LastUpdatedTime;
                if (Now.IsPlaying && updated.Year > 2000)
                {
                    double ago = (DateTimeOffset.Now - updated).TotalSeconds;
                    if (ago > 0 && ago < 3600) pos += ago;
                }
                Now.Duration = Math.Max(0, duration);
                Now.BasePosition = Math.Max(0, pos);
                Now.BaseTime = DateTime.UtcNow;
            }
            catch { }
        }

        private async void LoadThumbnail(IRandomAccessStreamReference reference, string id)
        {
            try
            {
                using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
                ulong size = stream.Size;
                if (size == 0 || size > 20_000_000) return;
                var bytes = new byte[size];
                using (var reader = new DataReader(stream))
                {
                    await reader.LoadAsync((uint)size);
                    reader.ReadBytes(bytes);
                }
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.DecodePixelWidth = 256;
                image.EndInit();
                image.Freeze();
                if (Now.TrackId != id) return;
                Now.Artwork = image;
                Now.Accent = AccentOf(image);
            }
            catch (Exception e)
            {
                Log.Write("Cover failed: " + e.Message);
            }
        }

        // ---- Controls ----

        public async void PlayPause()
        {
            var s = session;
            if (s == null) return;
            Now.BasePosition = Now.Position(DateTime.UtcNow);
            Now.BaseTime = DateTime.UtcNow;
            Now.IsPlaying = !Now.IsPlaying;
            try { await s.TryTogglePlayPauseAsync(); } catch { }
        }

        public async void Next()
        {
            var s = session;
            if (s == null) return;
            try { await s.TrySkipNextAsync(); } catch { }
        }

        public async void Previous()
        {
            var s = session;
            if (s == null) return;
            try { await s.TrySkipPreviousAsync(); } catch { }
        }

        public async void Seek(double fraction)
        {
            var s = session;
            if (s == null || Now.Duration <= 0) return;
            double seconds = Math.Clamp(fraction * Now.Duration, 0, Math.Max(0, Now.Duration - 1));
            Now.BasePosition = seconds;
            Now.BaseTime = DateTime.UtcNow;
            try { await s.TryChangePlaybackPositionAsync((long)(seconds * 10_000_000)); } catch { }
        }

        /// <summary>Friendly name of the app that plays.</summary>
        public static string SourceName(string appId)
        {
            string l = appId.ToLowerInvariant();
            if (l.Contains("spotify")) return "Spotify";
            if (l.Contains("msedge") || l.Contains("microsoftedge")) return "Edge";
            if (l.Contains("chrome")) return "Chrome";
            if (l.Contains("firefox") || l == "308046b0af4a39cb") return "Firefox";
            if (l.Contains("opera")) return "Opera";
            if (l.Contains("yandex")) return Lang.L("Яндекс", "Yandex");
            if (l.Contains("zunemusic")) return Lang.L("Медиаплеер", "Media Player");
            if (l.Contains("applemusic") || l.Contains("appleinc")) return "Apple Music";
            if (l.Contains("vlc")) return "VLC";
            if (l.Contains("telegram")) return "Telegram";
            if (l.Contains("discord")) return "Discord";
            if (l.Contains("soundcloud")) return "SoundCloud";
            if (l.Contains("deezer")) return "Deezer";
            string name = appId;
            int bang = name.IndexOf('!');
            if (bang >= 0) name = name.Substring(bang + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            return name.Length > 18 ? name.Substring(0, 18) : name;
        }

        /// <summary>A bright, saturated version of the cover's average colour, for glows and bars.</summary>
        public static Color AccentOf(BitmapSource src)
        {
            try
            {
                var scaled = new TransformedBitmap(src, new ScaleTransform(8.0 / src.PixelWidth, 8.0 / src.PixelHeight));
                var conv = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                var px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);
                double r = 0, g = 0, b = 0, wsum = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
                    double sat = Math.Max(pr, Math.Max(pg, pb)) - Math.Min(pr, Math.Min(pg, pb));
                    double wgt = 0.05 + sat * sat * 4;
                    r += pr * wgt; g += pg * wgt; b += pb * wgt; wsum += wgt;
                }
                if (wsum <= 0) return NowPlaying.DefaultAccent;
                ToHsv(r / wsum, g / wsum, b / wsum, out double hh, out double ss, out double vv);
                ss = Math.Min(0.85, Math.Max(0.35, ss * 1.3));
                vv = Math.Max(0.82, vv);
                FromHsv(hh, ss, vv, out double rr, out double gg, out double bb);
                return Color.FromRgb((byte)(rr * 255), (byte)(gg * 255), (byte)(bb * 255));
            }
            catch { return NowPlaying.DefaultAccent; }
        }

        private static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 1e-9) { h = 0; return; }
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }

        private static void FromHsv(double h, double s, double v, out double r, out double g, out double b)
        {
            double c = v * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = v - c;
            int sector = (int)Math.Floor(h * 6) % 6;
            (r, g, b) = sector switch
            {
                0 => (c, x, 0.0),
                1 => (x, c, 0.0),
                2 => (0.0, c, x),
                3 => (0.0, x, c),
                4 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            r += m; g += m; b += m;
        }
    }
}
