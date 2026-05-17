using System.Runtime.InteropServices;

namespace HidReorder.Core;

public sealed class HidMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize, dwFlags;
        public uint dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons, dwButtonNumber, dwPOV;
        public uint dwReserved1, dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct JOYCAPS
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
    private static extern int joyGetDevCaps(uint id, ref JOYCAPS caps, uint size);

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(uint id, ref JOYINFOEX info);

    private const int  JOYERR_NOERROR = 0;
    private const uint JOY_RETURNALL  = 0xFF;

    private readonly VidResolver _resolver;

    public int Threshold { get; set; } = 5;

    public HidMonitor(VidResolver resolver) => _resolver = resolver;

    public DeviceReading[] Poll()
    {
        var capsSize = (uint)Marshal.SizeOf(new JOYCAPS());
        var infoSize = (uint)Marshal.SizeOf(new JOYINFOEX());
        var result   = new List<DeviceReading>();

        for (uint id = 0; id < 16; id++)
        {
            var caps = new JOYCAPS();
            if (joyGetDevCaps(id, ref caps, capsSize) != JOYERR_NOERROR) continue;

            var info = new JOYINFOEX { dwSize = infoSize, dwFlags = JOY_RETURNALL };
            if (joyGetPosEx(id, ref info) != JOYERR_NOERROR) continue;

            var vid   = $"{caps.wMid:X4}";
            var pid   = $"{caps.wPid:X4}";
            var label = $"{_resolver.Resolve(vid, pid)}  [VID_{vid}&PID_{pid}]";
            var axes  = ReadAxes(caps, info);

            if (axes.Length > 0)
                result.Add(new DeviceReading(label, axes));
        }

        return [.. result];
    }

    private AxisReading[] ReadAxes(JOYCAPS caps, JOYINFOEX info)
    {
        // Map axis name → (current value, min, max)
        (string Name, uint Val, uint Min, uint Max)[] raw =
        [
            ("X", info.dwXpos, caps.wXmin, caps.wXmax),
            ("Y", info.dwYpos, caps.wYmin, caps.wYmax),
            ("Z", info.dwZpos, caps.wZmin, caps.wZmax),
            ("R", info.dwRpos, caps.wRmin, caps.wRmax),
            ("U", info.dwUpos, caps.wUmin, caps.wUmax),
            ("V", info.dwVpos, caps.wVmin, caps.wVmax),
        ];

        var readings = new List<AxisReading>();
        int limit    = Math.Min((int)caps.wNumAxes, raw.Length);

        for (int i = 0; i < limit; i++)
        {
            var (name, val, min, max) = raw[i];
            if (max == 0 && min == 0) continue;

            int pct     = max > min ? (int)((val - min) / (double)(max - min) * 100) : 50;
            pct         = Math.Clamp(pct, 0, 100);
            bool drifts = Math.Abs(pct - 50) > Threshold;

            readings.Add(new AxisReading(name, pct, drifts));
        }

        return [.. readings];
    }
}
