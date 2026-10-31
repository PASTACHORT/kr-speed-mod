using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KRVSpeed;

/// <summary>
/// Finds cocos2d objects in Kingdom Rush Vengeance through MSVC RTTI and reads/writes a few fields.
/// - Scheduler::_timeScale (float) scales every scheduled update, i.e. the whole game clock.
/// - Director's running scene tells whether a battle (KRGameplayScene) is on screen.
/// </summary>
sealed class GameMemory : IDisposable
{
    public const string ProcessName = "Kingdom Rush Vengeance";
    const int TimeScaleOffset = 0x28;     // Scheduler: vtable, Ref fields, then _timeScale
    const int RunningSceneOffset = 0x150; // Director::_runningScene

    public Process Process { get; }
    readonly IntPtr _h;
    readonly ulong _moduleBase;
    readonly Dictionary<string, ulong> _vtables = new();
    ulong _scheduler, _director;

    GameMemory(Process p, IntPtr handle)
    {
        Process = p;
        _h = handle;
        _moduleBase = (ulong)p.MainModule!.BaseAddress;
    }

    public static GameMemory? TryAttach()
    {
        var p = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        if (p == null || p.HasExited) return null;
        var h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION
                                   | Native.PROCESS_QUERY_INFORMATION, false, p.Id);
        if (h == IntPtr.Zero) return null;
        var gm = new GameMemory(p, h);
        try
        {
            var exe = p.MainModule!.FileName!;
            foreach (var (name, rva) in Rtti.FindVtables(exe, "Scheduler@cocos2d", "Director@cocos2d", "KRGameplayScene"))
                gm._vtables[name] = gm._moduleBase + rva;
            if (gm._vtables.Count < 3) { gm.Dispose(); return null; }
            if (!gm.FindObjects()) { gm.Dispose(); return null; }
            return gm;
        }
        catch { gm.Dispose(); return null; }
    }

    bool FindObjects()
    {
        var hits = ScanForVtables(_vtables["Scheduler@cocos2d"], _vtables["Director@cocos2d"]);
        _scheduler = hits[0].FirstOrDefault(a => ReadFloat(a + TimeScaleOffset) is float f && IsSaneScale(f));
        _director = hits[1].FirstOrDefault();
        return _scheduler != 0 && _director != 0;
    }

    static bool IsSaneScale(float f) => f >= 0f && f <= 64f;

    public bool Alive => !Process.HasExited;

    public float? TimeScale
    {
        get { var f = ReadFloat(_scheduler + TimeScaleOffset); return f is float v && IsSaneScale(v) ? v : null; }
        set { if (value is float v) WriteFloat(_scheduler + TimeScaleOffset, v); }
    }

    /// <summary>Address of the running scene, and whether it is a battle.</summary>
    public (ulong scene, bool battle) Scene()
    {
        var scene = ReadU64(_director + RunningSceneOffset) ?? 0;
        if (scene == 0) return (0, false);
        var vt = ReadU64(scene) ?? 0;
        return (scene, vt == _vtables["KRGameplayScene"]);
    }

    // ---- memory helpers ------------------------------------------------------------------

    List<ulong>[] ScanForVtables(params ulong[] vtables)
    {
        var result = vtables.Select(_ => new List<ulong>()).ToArray();
        ulong addr = 0;
        var buf = new byte[1 << 22];
        while (Native.VirtualQueryEx(_h, (IntPtr)addr, out var mbi, (IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>()) != IntPtr.Zero)
        {
            ulong regionBase = (ulong)mbi.BaseAddress, size = (ulong)mbi.RegionSize;
            if (mbi.State == 0x1000 && mbi.Type == 0x20000 && (mbi.Protect == 0x04 || mbi.Protect == 0x40))
            {
                for (ulong off = 0; off < size; off += (ulong)buf.Length)
                {
                    int n = (int)Math.Min((ulong)buf.Length, size - off);
                    if (!Native.ReadProcessMemory(_h, (IntPtr)(regionBase + off), buf, (IntPtr)n, out var got)) continue;
                    int len = (int)got;
                    for (int i = 0; i + 8 <= len; i += 8)
                    {
                        ulong q = BitConverter.ToUInt64(buf, i);
                        for (int k = 0; k < vtables.Length; k++)
                            if (q == vtables[k]) result[k].Add(regionBase + off + (ulong)i);
                    }
                }
            }
            addr = regionBase + size;
            if (addr >= 0x7FFFFFFFFFFF) break;
        }
        return result;
    }

    ulong? ReadU64(ulong a)
    {
        var b = new byte[8];
        return Native.ReadProcessMemory(_h, (IntPtr)a, b, (IntPtr)8, out var got) && (long)got == 8 ? BitConverter.ToUInt64(b) : null;
    }

    float? ReadFloat(ulong a)
    {
        var b = new byte[4];
        return Native.ReadProcessMemory(_h, (IntPtr)a, b, (IntPtr)4, out var got) && (long)got == 4 ? BitConverter.ToSingle(b) : null;
    }

    void WriteFloat(ulong a, float v)
    {
        var b = BitConverter.GetBytes(v);
        Native.WriteProcessMemory(_h, (IntPtr)a, b, (IntPtr)4, out _);
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) Native.CloseHandle(_h);
    }
}

/// <summary>Static RTTI lookup in the exe file: class name -> vtable RVA (MSVC x64 layout).</summary>
static class Rtti
{
    public static IEnumerable<(string, ulong)> FindVtables(string exePath, params string[] names)
    {
        var d = File.ReadAllBytes(exePath);
        int pe = BitConverter.ToInt32(d, 0x3C);
        int nsec = BitConverter.ToUInt16(d, pe + 6);
        int optSize = BitConverter.ToUInt16(d, pe + 20);
        int opt = pe + 24;
        ulong imageBase = BitConverter.ToUInt64(d, opt + 24);
        var secs = new List<(uint va, uint raw, uint rsize)>();
        for (int i = 0; i < nsec; i++)
        {
            int o = opt + optSize + i * 40;
            secs.Add((BitConverter.ToUInt32(d, o + 12), BitConverter.ToUInt32(d, o + 20), BitConverter.ToUInt32(d, o + 16)));
        }
        uint? Off2Rva(int off)
        {
            foreach (var s in secs)
                if (off >= s.raw && off < s.raw + s.rsize) return (uint)(s.va + off - s.raw);
            return null;
        }

        foreach (var name in names)
        {
            var tag = Encoding.ASCII.GetBytes(".?AV" + name + "@@\0");
            int idx = IndexOf(d, tag, 0);
            if (idx < 0) continue;
            var tdRva = Off2Rva(idx - 16);
            if (tdRva == null) continue;
            var needle = BitConverter.GetBytes(tdRva.Value);
            for (int j = IndexOf(d, needle, 0); j >= 0; j = IndexOf(d, needle, j + 1))
            {
                int col = j - 12;
                if (col < 0) continue;
                uint sig = BitConverter.ToUInt32(d, col), offs = BitConverter.ToUInt32(d, col + 4);
                uint self = BitConverter.ToUInt32(d, col + 20);
                if (sig != 1 || offs != 0 || Off2Rva(col) != self) continue;
                var colVa = BitConverter.GetBytes(imageBase + self);
                int k = IndexOf(d, colVa, 0);
                if (k < 0) continue;
                var vtRva = Off2Rva(k);
                if (vtRva != null) { yield return (name, vtRva.Value + 8UL); break; }
            }
        }
    }

    static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        var span = hay.AsSpan(start);
        int i = span.IndexOf(needle);
        return i < 0 ? -1 : start + i;
    }
}

static class Native
{
    public const uint PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_OPERATION = 0x0008,
        PROCESS_QUERY_INFORMATION = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
