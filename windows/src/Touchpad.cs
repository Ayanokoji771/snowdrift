using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Snowdrift
{
    /// <summary>Finger positions from a Precision Touchpad through Raw Input. Read only: Windows keeps
    /// moving the pointer and handling taps as usual. Nothing is stored or sent anywhere.</summary>
    internal sealed class Touchpad : IDisposable
    {
        /// <summary>Fingers touching the pad (mm), and when (seconds).</summary>
        public event Action<List<Contact>, double> Frame;

        public float WidthMm { get; private set; } = 100;
        public float HeightMm { get; private set; } = 65;

        private HwndSource window;
        private readonly Dictionary<IntPtr, Pad> pads = new Dictionary<IntPtr, Pad>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private IntPtr buffer;
        private int bufferSize;
        private byte[] report = new byte[64];
        private readonly ushort[] usages = new ushort[64];

        private const ushort PageDigitizer = 0x0D, UsageTouchPad = 0x05;
        private const ushort PageGeneric = 0x01, UsageX = 0x30, UsageY = 0x31;
        private const ushort UsageTip = 0x42, UsageConfidence = 0x47, UsageContactId = 0x51, UsageContactCount = 0x54;
        private const int HidpInput = 0;
        private const int Success = 0x00110000;

        private sealed class Pad
        {
            public IntPtr Preparsed;
            public readonly List<ushort> Fingers = new List<ushort>();
            public ushort CountCollection;
            public bool HasCount, HasConfidence;
            public int XMin, XMax, YMin, YMax;
            public float WidthMm = 100, HeightMm = 65;
            public int Expected;
            public readonly List<(int id, int x, int y, bool down)> Pending = new List<(int, int, int, bool)>();
        }

        public bool Start()
        {
            try
            {
                var p = new HwndSourceParameters("SnowdriftTouchpad")
                {
                    Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW,
                };
                window = new HwndSource(p);
                window.AddHook(Hook);
                var dev = new RAWINPUTDEVICE[]
                {
                    new RAWINPUTDEVICE { UsagePage = PageDigitizer, Usage = UsageTouchPad, Flags = RIDEV_INPUTSINK, Target = window.Handle },
                };
                if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                {
                    Log.Write("Touchpad: raw input registration failed " + Marshal.GetLastWin32Error());
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Write("Touchpad: " + e.Message);
                return false;
            }
        }

        /// <summary>Whether a Precision Touchpad is connected.</summary>
        public static bool Exists()
        {
            try
            {
                uint count = 0;
                uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
                GetRawInputDeviceList(null, ref count, size);
                if (count == 0) return false;
                var list = new RAWINPUTDEVICELIST[count];
                if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return false;
                IntPtr info = Marshal.AllocHGlobal(64);
                try
                {
                    foreach (var d in list)
                    {
                        if (d.Type != RIM_TYPEHID) continue;
                        Marshal.WriteInt32(info, 0, 32);
                        uint isz = 32;
                        if (GetRawInputDeviceInfo(d.Device, RIDI_DEVICEINFO, info, ref isz) == uint.MaxValue) continue;
                        ushort page = (ushort)Marshal.ReadInt16(info, 20), usage = (ushort)Marshal.ReadInt16(info, 22);
                        if (page == PageDigitizer && usage == UsageTouchPad) return true;
                    }
                }
                finally { Marshal.FreeHGlobal(info); }
            }
            catch { }
            return false;
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_INPUT)
            {
                try { Read(lParam); }
                catch (Exception e) { Log.Write("Touchpad read: " + e.Message); }
            }
            return IntPtr.Zero;
        }

        private void Read(IntPtr raw)
        {
            int header = 8 + 2 * IntPtr.Size;
            uint size = 0;
            GetRawInputData(raw, RID_INPUT, IntPtr.Zero, ref size, (uint)header);
            if (size == 0) return;
            if (size > bufferSize)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                bufferSize = (int)Math.Max(size, 256);
                buffer = Marshal.AllocHGlobal(bufferSize);
            }
            if (GetRawInputData(raw, RID_INPUT, buffer, ref size, (uint)header) == uint.MaxValue) return;
            if (Marshal.ReadInt32(buffer, 0) != RIM_TYPEHID) return;
            IntPtr device = Marshal.ReadIntPtr(buffer, 8);
            var pad = Get(device);
            if (pad == null) return;
            int sizeHid = Marshal.ReadInt32(buffer, header), count = Marshal.ReadInt32(buffer, header + 4);
            if (sizeHid <= 0 || count <= 0) return;
            if (report.Length < sizeHid) report = new byte[sizeHid];
            for (int r = 0; r < count; r++)
            {
                Marshal.Copy(buffer + header + 8 + r * sizeHid, report, 0, sizeHid);
                Parse(pad, (uint)sizeHid);
            }
        }

        /// <summary>One HID report. Touchpads send all fingers in one report, or a few per report
        /// with the total count in the first ("hybrid" mode).</summary>
        private void Parse(Pad pad, uint len)
        {
            int cc = -1;
            if (pad.HasCount && HidP_GetUsageValue(HidpInput, PageDigitizer, pad.CountCollection, UsageContactCount, out uint c, pad.Preparsed, report, len) == Success)
                cc = (int)c;
            if (cc > 0 || pad.Expected == 0)
            {
                pad.Pending.Clear();
                pad.Expected = cc >= 0 ? cc : pad.Fingers.Count;
            }
            foreach (ushort lc in pad.Fingers)
            {
                if (pad.Pending.Count >= pad.Expected) break;
                if (HidP_GetUsageValue(HidpInput, PageGeneric, lc, UsageX, out uint x, pad.Preparsed, report, len) != Success) continue;
                if (HidP_GetUsageValue(HidpInput, PageGeneric, lc, UsageY, out uint y, pad.Preparsed, report, len) != Success) continue;
                HidP_GetUsageValue(HidpInput, PageDigitizer, lc, UsageContactId, out uint id, pad.Preparsed, report, len);
                bool tip = false, confident = !pad.HasConfidence;
                uint n = (uint)usages.Length;
                if (HidP_GetUsages(HidpInput, PageDigitizer, lc, usages, ref n, pad.Preparsed, report, len) == Success)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (usages[i] == UsageTip) tip = true;
                        else if (usages[i] == UsageConfidence) confident = true;
                    }
                }
                pad.Pending.Add(((int)id, (int)x, (int)y, tip && confident));
            }
            if (pad.Pending.Count < pad.Expected) return;

            var frame = new List<Contact>(pad.Pending.Count);
            float sx = pad.WidthMm / Math.Max(1, pad.XMax - pad.XMin), sy = pad.HeightMm / Math.Max(1, pad.YMax - pad.YMin);
            foreach (var f in pad.Pending)
                if (f.down) frame.Add(new Contact(f.id, (f.x - pad.XMin) * sx, (f.y - pad.YMin) * sy));
            pad.Pending.Clear();
            pad.Expected = 0;
            WidthMm = pad.WidthMm;
            HeightMm = pad.HeightMm;
            Frame?.Invoke(frame, clock.Elapsed.TotalSeconds);
        }

        private Pad Get(IntPtr device)
        {
            if (pads.TryGetValue(device, out var pad)) return pad;
            pad = Describe(device);
            pads[device] = pad;
            if (pad != null) Log.Write($"Touchpad: {pad.Fingers.Count} finger slots, {pad.WidthMm:0}×{pad.HeightMm:0} mm");
            return pad;
        }

        private static Pad Describe(IntPtr device)
        {
            uint size = 0;
            GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size == 0) return null;
            IntPtr data = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, data, ref size) == uint.MaxValue)
            {
                Marshal.FreeHGlobal(data);
                return null;
            }
            var caps = new byte[64];
            if (HidP_GetCaps(data, caps) != Success) { Marshal.FreeHGlobal(data); return null; }
            ushort nButtons = BitConverter.ToUInt16(caps, 46), nValues = BitConverter.ToUInt16(caps, 48);
            var pad = new Pad { Preparsed = data };

            var values = new byte[Math.Max(1, (int)nValues) * 72];
            ushort vlen = nValues;
            if (nValues > 0 && HidP_GetValueCaps(HidpInput, values, ref vlen, data) == Success)
            {
                bool gotX = false, gotY = false;
                for (int i = 0; i < vlen; i++)
                {
                    int o = i * 72;
                    ushort page = BitConverter.ToUInt16(values, o), lc = BitConverter.ToUInt16(values, o + 6);
                    bool range = values[o + 12] != 0;
                    ushort usage = BitConverter.ToUInt16(values, o + 56), usageMax = range ? BitConverter.ToUInt16(values, o + 58) : usage;
                    bool Has(ushort u) => u >= usage && u <= usageMax;
                    int lmin = BitConverter.ToInt32(values, o + 40), lmax = BitConverter.ToInt32(values, o + 44);
                    int pmin = BitConverter.ToInt32(values, o + 48), pmax = BitConverter.ToInt32(values, o + 52);
                    uint unitsExp = BitConverter.ToUInt32(values, o + 32), units = BitConverter.ToUInt32(values, o + 36);
                    if (page == PageGeneric && Has(UsageX))
                    {
                        if (!pad.Fingers.Contains(lc)) pad.Fingers.Add(lc);
                        if (!gotX) { gotX = true; pad.XMin = lmin; pad.XMax = lmax; pad.WidthMm = Millimetres(pmax - pmin, units, unitsExp, 100); }
                    }
                    else if (page == PageGeneric && Has(UsageY) && !gotY)
                    {
                        gotY = true;
                        pad.YMin = lmin;
                        pad.YMax = lmax;
                        pad.HeightMm = Millimetres(pmax - pmin, units, unitsExp, 0);
                    }
                    else if (page == PageDigitizer && Has(UsageContactCount))
                    {
                        pad.HasCount = true;
                        pad.CountCollection = lc;
                    }
                }
                if (pad.HeightMm <= 0)
                    pad.HeightMm = pad.WidthMm * Math.Max(1, pad.YMax - pad.YMin) / Math.Max(1, pad.XMax - pad.XMin);
            }

            if (nButtons > 0)
            {
                var buttons = new byte[nButtons * 72];
                ushort blen = nButtons;
                if (HidP_GetButtonCaps(HidpInput, buttons, ref blen, data) == Success)
                {
                    for (int i = 0; i < blen; i++)
                    {
                        int o = i * 72;
                        if (BitConverter.ToUInt16(buttons, o) != PageDigitizer) continue;
                        bool range = buttons[o + 12] != 0;
                        ushort lo = BitConverter.ToUInt16(buttons, o + 56), hi = range ? BitConverter.ToUInt16(buttons, o + 58) : lo;
                        if (UsageConfidence >= lo && UsageConfidence <= hi) pad.HasConfidence = true;
                    }
                }
            }

            pad.Fingers.Sort();
            if (pad.Fingers.Count == 0 || pad.XMax <= pad.XMin || pad.YMax <= pad.YMin)
            {
                Marshal.FreeHGlobal(data);
                return null;
            }
            return pad;
        }

        /// <summary>HID physical extent → millimetres (centimetre or inch units with a power-of-ten exponent).</summary>
        private static float Millimetres(int extent, uint units, uint unitsExp, float fallback)
        {
            if (extent <= 0) return fallback;
            int e = (int)(unitsExp & 0xF);
            if (e >= 8) e -= 16;
            double v = extent * Math.Pow(10, e);
            double mm = (units & 0xFF) == 0x13 ? v * 25.4 : (units & 0xFF) == 0x11 ? v * 10 : -1;
            return mm > 20 && mm < 400 ? (float)mm : fallback;
        }

        public void Dispose()
        {
            window?.Dispose();
            window = null;
            foreach (var p in pads.Values) if (p != null && p.Preparsed != IntPtr.Zero) Marshal.FreeHGlobal(p.Preparsed);
            pads.Clear();
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
        }

        // ---- Win32 ----

        private const int WM_INPUT = 0x00FF;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_PREPARSEDDATA = 0x20000005;
        private const uint RIDI_DEVICEINFO = 0x2000000b;
        private const int RIM_TYPEHID = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST
        {
            public IntPtr Device;
            public int Type;
        }

        [DllImport("user32", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32")]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
        [DllImport("user32")]
        private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);

        [DllImport("hid")] private static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);
        [DllImport("hid")] private static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, IntPtr preparsed);
        [DllImport("hid")] private static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort length, IntPtr preparsed);
        [DllImport("hid")]
        private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value,
                                                     IntPtr preparsed, byte[] report, uint reportLength);
        [DllImport("hid")]
        private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, ushort[] usageList, ref uint usageLength,
                                                 IntPtr preparsed, byte[] report, uint reportLength);
    }
}
