using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VisorMonitor;
using VisorSdk;

// visor のアプリ「リソース」: CPU・GPU・メモリ・ビデオメモリ・VR のコマの時間と、重いプロセスの一覧（終えることもできる）。
// 窓が見えている間だけ 1 秒おきに取って描く。VR のコマは visor に頼んで読む（vr.stats）。

const int Rows = 10;

var app = new VisorApp();
var stats = new SystemStats();
var gate = new object();
var sortByMem = false;
string? confirm = null;
SystemStats.Snapshot? last = null;
List<SystemStats.Proc> procs = [];
JsonNode? vr = null;

app.Event += id =>
{
    lock (gate)
    {
        switch (id)
        {
            case "sort:cpu": sortByMem = false; break;
            case "sort:mem": sortByMem = true; break;
            default:
                // 終えるのは 2 回押したとき（1 回目で「終える？」にする）
                if (id.StartsWith("end:"))
                {
                    var name = id[4..];
                    if (confirm == name)
                    {
                        confirm = null;
                        End(name);
                    }
                    else confirm = name;
                }
                break;
        }
    }
    Draw();
};

app.Tool("system_status", "PC の様子（CPU・GPU・メモリ・ビデオメモリ）と重いプロセスを見る（「重い」「カクつく」と言われたとき）。", async _ =>
{
    // 前の値との差で CPU を出すので、少し空けて 2 回取る
    stats.Sample();
    stats.Processes();
    await Task.Delay(800);
    var s = stats.Sample();
    var p = stats.Processes();
    return Summary(s, p);
});
app.Tool("end_process", "プロセスを名前で終える（同じ名前を全部）。保存していない作業が消えることがあるので、必ず本人に確かめてから使う。", a =>
{
    var name = a["name"]?.GetValue<string>() ?? throw new ArgumentException("name が無い");
    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
    var ended = End(name);
    return ended > 0 ? $"{name} を {ended} 個終えた" : $"{name} は動いていない";
}, ("name", "string"));
app.PublishTools();
Draw();

_ = Task.Run(async () =>
{
    while (true)
    {
        if (app.IsVisible)
        {
            var s = stats.Sample();
            var p = stats.Processes();
            JsonNode? v = null;
            try { v = await app.Call("vr.stats"); }
            catch (Exception) { }
            lock (gate)
            {
                last = s;
                procs = p;
                vr = v;
            }
            Draw();
        }
        await Task.Delay(1000);
    }
});

await app.Run();
stats.Dispose();

void Draw()
{
    JsonObject ui;
    lock (gate) ui = Build();
    app.Render(ui);
    lock (gate)
        if (last != null) app.Describe(Summary(last, procs));
}

JsonObject Build()
{
    var s = last;
    var col = Ui.Column(
        Meter("CPU", s == null ? "-" : $"{s.Cpu:F0}%", (s?.Cpu ?? 0) / 100),
        Meter("GPU", s == null ? "-" : $"{s.Gpu:F0}%", (s?.Gpu ?? 0) / 100),
        Meter("メモリ", s == null ? "-" : $"{s.MemUsedGb:F1} / {s.MemTotalGb:F0} GB", s == null ? 0 : s.MemUsedGb / Math.Max(1, s.MemTotalGb)),
        Meter("ビデオメモリ", s == null ? "-" : $"{s.VramUsedGb:F1} GB", Math.Min(1, (s?.VramUsedGb ?? 0) / 16)),
        Ui.Text(vr is JsonObject v
            ? $"VR: GPU {v["gpuMs"]} ms・落ちたコマ {v["dropped"]}・作り直したコマ {v["reprojected"]}"
            : "VR: つながっていない", "small", "dim"),
        Ui.Divider(),
        Ui.Row(
            Ui.Text("プロセス", bold: true).Align("center"),
            Ui.Spacer().Grow(),
            Ui.Button("sort:cpu", "CPU 順", style: sortByMem ? null : "selected", small: true),
            Ui.Button("sort:mem", "メモリ順", style: sortByMem ? "selected" : null, small: true)),
        ProcList()).Gap(18);
    return col;
}

static JsonObject Meter(string name, string value, double ratio) =>
    Ui.Column(
        Ui.Row(Ui.Text(name, "small", "dim"), Ui.Spacer().Grow(), Ui.Text(value, "small")),
        Ui.Progress(Math.Clamp(ratio, 0, 1))).Gap(8);

JsonObject ProcList()
{
    var list = Ui.Column().Gap(4);
    var rows = (JsonArray)list["children"]!;
    // 同じ名前のプロセスはまとめる（Chrome など）
    foreach (var gr in procs.GroupBy(p => p.Name)
        .Select(g => (Name: g.Key, Cpu: g.Sum(p => p.Cpu), Mem: g.Sum(p => p.MemMb), Count: g.Count()))
        .Where(g => g.Name != "Idle")
        .OrderByDescending(g => sortByMem ? g.Mem : g.Cpu)
        .Take(Rows))
    {
        var asking = confirm == gr.Name;
        rows.Add(Ui.Row(
            Ui.Text(gr.Count > 1 ? $"{gr.Name}（{gr.Count}）" : gr.Name, "small").Grow().Align("center"),
            Ui.Text($"{gr.Cpu:F1}%", "small", "dim").Width(100).TextAlign("end").Align("center"),
            Ui.Text($"{gr.Mem:F0} MB", "small", "dim").Width(150).TextAlign("end").Align("center"),
            asking ? Ui.Button($"end:{gr.Name}", "終える？", style: "danger", small: true).Align("center")
                   : Ui.Button($"end:{gr.Name}", icon: "close").Align("center")).Gap(12));
    }
    return list;
}

static int End(string name)
{
    var ended = 0;
    foreach (var p in Process.GetProcessesByName(name))
    {
        using (p)
        {
            try
            {
                p.Kill();
                ended++;
            }
            catch (Exception) { }
        }
    }
    return ended;
}

static string Summary(SystemStats.Snapshot s, List<SystemStats.Proc> procs)
{
    var sb = new StringBuilder();
    sb.AppendLine($"CPU {s.Cpu:F0}%、GPU {s.Gpu:F0}%、メモリ {s.MemUsedGb:F1}/{s.MemTotalGb:F0} GB、ビデオメモリ {s.VramUsedGb:F1} GB");
    sb.AppendLine("重いプロセス（CPU %・メモリ MB）:");
    foreach (var gr in procs.GroupBy(p => p.Name).Where(g => g.Key != "Idle")
        .Select(g => (g.Key, Cpu: g.Sum(p => p.Cpu), Mem: g.Sum(p => p.MemMb))).OrderByDescending(g => g.Cpu + g.Mem / 1000).Take(12))
        sb.AppendLine($"- {gr.Key}: {gr.Cpu:F1}%・{gr.Mem:F0} MB");
    return sb.ToString();
}
