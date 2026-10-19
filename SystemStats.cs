using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VisorMonitor;

/// <summary>PC の様子（CPU・GPU・メモリ・ビデオメモリ）と、プロセスの一覧を取る。CPU と GPU は Windows のパフォーマンスカウンター（PDH）。</summary>
[SupportedOSPlatform("windows")]
sealed partial class SystemStats : IDisposable
{
    nint _query;
    nint _cpu, _gpu, _vram;
    readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _lastCpu = [];

    public SystemStats()
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0) return;
        PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Utility", 0, out _cpu);
        // GPU は「3D」のエンジンの使用率を、ビデオメモリはアダプターごとの使用量を足す
        PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out _gpu);
        PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _vram);
        PdhCollectQueryData(_query);
    }

    public sealed record Snapshot(double Cpu, double Gpu, double MemUsedGb, double MemTotalGb, double VramUsedGb);

    /// <summary>前に呼んだときからの平均（初めは 0）。1 秒くらいおきに呼ぶ。</summary>
    readonly object _gate = new();

    public Snapshot Sample()
    {
        lock (_gate) return SampleCore();
    }

    Snapshot SampleCore()
    {
        double cpu = 0, gpu = 0, vram = 0;
        if (_query != 0 && PdhCollectQueryData(_query) == 0)
        {
            cpu = Single(_cpu);
            gpu = Math.Min(100, Sum(_gpu));
            vram = Sum(_vram) / 1024 / 1024 / 1024;
        }
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        const double gb = 1024.0 * 1024 * 1024;
        return new Snapshot(Math.Min(100, cpu), gpu, (mem.ullTotalPhys - mem.ullAvailPhys) / gb, mem.ullTotalPhys / gb, vram);
    }

    public sealed record Proc(int Pid, string Name, double Cpu, double MemMb);

    /// <summary>プロセスの一覧（CPU は前に呼んだときからの平均の %、全部のコアで 100）。</summary>
    public List<Proc> Processes()
    {
        lock (_gate) return ProcessesCore();
    }

    List<Proc> ProcessesCore()
    {
        var now = DateTime.UtcNow;
        var list = new List<Proc>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var t = p.TotalProcessorTime;
                    seen.Add(p.Id);
                    var cpu = 0.0;
                    if (_lastCpu.TryGetValue(p.Id, out var last))
                    {
                        var dt = (now - last.At).TotalMilliseconds;
                        if (dt > 0) cpu = (t - last.Cpu).TotalMilliseconds / dt / Environment.ProcessorCount * 100;
                    }
                    _lastCpu[p.Id] = (t, now);
                    list.Add(new Proc(p.Id, p.ProcessName, cpu, p.WorkingSet64 / 1024.0 / 1024));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 権限の無いプロセス（システムのもの）は飛ばす
                }
            }
        }
        foreach (var pid in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _lastCpu.Remove(pid);
        return list;
    }

    double Single(nint counter)
    {
        if (counter == 0) return 0;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v) == 0 ? v.doubleValue : 0;
    }

    /// <summary>ワイルドカードの付いたカウンターの、全部の値の和。</summary>
    unsafe double Sum(nint counter)
    {
        if (counter == 0) return 0;
        uint size = 0, count = 0;
        var r = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, ref count, null);
        if (r != PDH_MORE_DATA || size == 0) return 0;
        var buffer = new byte[size];
        fixed (byte* b = buffer)
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, ref count, b) != 0) return 0;
            var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)b;
            double sum = 0;
            for (var i = 0; i < count; i++)
                if (items[i].FmtValue.CStatus == 0) sum += items[i].FmtValue.doubleValue;
            return sum;
        }
    }

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
    }

    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public char* szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? source, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [LibraryImport("pdh.dll")]
    private static unsafe partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, ref uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
