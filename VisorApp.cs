// visor のアプリを C# で作るための部品（1 ファイル）。アプリのリポジトリにこのファイルを写して使う。
// 元は visor のリポジトリの sdk/dotnet/VisorApp.cs。話し方の決まりは visor の docs/extensions.md。
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisorSdk;

/// <summary>
/// visor との受け渡し: 標準入出力で 1 行 1 つの JSON を送り受けする。Run で visor から来るものを読み続ける。
/// 送るのはどのスレッドからでもよい。来たもの（ボタン・見えているか・道具の呼び出し）は Run を回しているスレッドで渡す。
/// </summary>
public sealed class VisorApp
{
    readonly TextWriter _out;
    readonly object _gate = new();
    readonly Dictionary<string, Func<JsonObject, Task<string>>> _tools = [];
    readonly List<JsonObject> _toolList = [];
    readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _calls = [];
    int _nextCall;

    public VisorApp()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>このアプリの id（visor が入れたフォルダの名前）。</summary>
    public static string Id => Environment.GetEnvironmentVariable("VISOR_APP_ID") ?? "app";

    /// <summary>アプリが自分で残すもの（設定など）の置き場所。入れ直しても消えない。</summary>
    public static string DataDir => Environment.GetEnvironmentVariable("VISOR_APP_DATA") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>visor 自身の置き場所（%LOCALAPPDATA%\visor）。前の版の設定を引き継ぐときなどに読む。</summary>
    public static string? VisorDataDir => Environment.GetEnvironmentVariable("VISOR_DATA");

    /// <summary>ボタンが押された（ボタンの id）。</summary>
    public event Action<string>? Event;

    /// <summary>窓が見えるようになった・見えなくなった（見えていない間は更新を止めてよい）。</summary>
    public event Action<bool>? Visible;

    public bool IsVisible { get; private set; }

    // --- 送る ---

    /// <summary>窓の中身を入れ替える（Ui の部品で組んだもの）。</summary>
    public void Render(JsonNode ui) => Send(new JsonObject { ["type"] = "render", ["ui"] = ui });

    /// <summary>Claude が窓を読んだときに渡す文。</summary>
    public void Describe(string text) => Send(new JsonObject { ["type"] = "describe", ["text"] = text });

    /// <summary>visor の通知を出す。</summary>
    public void Notify(string title, string body = "") => Send(new JsonObject { ["type"] = "notify", ["title"] = title, ["body"] = body });

    /// <summary>窓を開く。</summary>
    public void Open() => Send(new JsonObject { ["type"] = "open" });

    /// <summary>
    /// Claude に渡す道具を足す（app_アプリ_名前 で見える）。parameters は 名前 → 型（string・number・boolean）。
    /// handler の返した文が Claude に渡る（20 秒以内に返す）。足し終えたら PublishTools で visor に知らせる。
    /// </summary>
    public void Tool(string name, string description, Func<JsonObject, Task<string>> handler, params (string Name, string Type)[] parameters)
    {
        _tools[name] = handler;
        var p = new JsonObject();
        foreach (var (n, t) in parameters) p[n] = t;
        _toolList.RemoveAll(t => t["name"]?.GetValue<string>() == name);
        _toolList.Add(new JsonObject { ["name"] = name, ["description"] = description, ["params"] = p });
    }

    public void Tool(string name, string description, Func<JsonObject, string> handler, params (string Name, string Type)[] parameters) =>
        Tool(name, description, a => Task.FromResult(handler(a)), parameters);

    public void PublishTools() => Send(new JsonObject { ["type"] = "tools", ["tools"] = new JsonArray(_toolList.Select(t => (JsonNode)t.DeepClone()).ToArray()) });

    /// <summary>visor に頼む（窓口と同じメソッド: vr.stats・mcp.call など）。</summary>
    public Task<JsonNode?> Call(string method, JsonObject? parameters = null)
    {
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        lock (_gate)
        {
            id = (++_nextCall).ToString();
            _calls[id] = tcs;
        }
        Send(new JsonObject { ["type"] = "call", ["call"] = id, ["method"] = method, ["params"] = parameters ?? [] });
        return tcs.Task;
    }

    void Send(JsonObject msg)
    {
        lock (_gate) _out.WriteLine(msg.ToJsonString());
    }

    // --- 受ける ---

    /// <summary>visor から来るものを読み続ける（visor が止めるか、標準入力が閉じるまで）。</summary>
    public async Task Run()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await input.ReadLineAsync() is { } line)
        {
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (msg == null) continue;
            switch (msg["type"]?.GetValue<string>())
            {
                case "event":
                    Event?.Invoke(msg["id"]?.GetValue<string>() ?? "");
                    break;
                case "visible":
                    IsVisible = msg["visible"]?.GetValue<bool>() == true;
                    Visible?.Invoke(IsVisible);
                    break;
                case "tool_call":
                    _ = AnswerTool(msg);
                    break;
                case "call_result":
                {
                    TaskCompletionSource<JsonNode?>? tcs;
                    lock (_gate) _calls.Remove(msg["call"]?.ToString() ?? "", out tcs);
                    if (tcs == null) break;
                    if (msg["error"] is { } err) tcs.TrySetException(new InvalidOperationException(err.ToString()));
                    else tcs.TrySetResult(msg["result"]?.DeepClone());
                    break;
                }
            }
        }
    }

    async Task AnswerTool(JsonObject msg)
    {
        var name = msg["name"]?.GetValue<string>() ?? "";
        string text;
        try
        {
            text = _tools.TryGetValue(name, out var h) ? await h(msg["arguments"] as JsonObject ?? []) : $"知らない道具: {name}";
        }
        catch (Exception ex)
        {
            text = $"失敗した: {ex.Message}";
        }
        Send(new JsonObject { ["type"] = "tool_result", ["call"] = msg["call"]?.DeepClone(), ["text"] = text });
    }
}

/// <summary>窓の中身の部品（docs/extensions.md の「画面の部品」）を組む。</summary>
public static class Ui
{
    public static JsonObject Column(params JsonNode?[] children) => new() { ["type"] = "column", ["children"] = Children(children) };
    public static JsonObject Row(params JsonNode?[] children) => new() { ["type"] = "row", ["children"] = Children(children) };

    /// <summary>size: small・body・title・large・display。color: dim・faint・accent・warning・error・good。</summary>
    public static JsonObject Text(string text, string? size = null, string? color = null, bool bold = false) =>
        Set(new JsonObject { ["type"] = "text", ["text"] = text }, ("size", size), ("color", color), ("weight", bold ? "bold" : null));

    public static JsonObject Markdown(string text) => new() { ["type"] = "markdown", ["text"] = text };

    /// <summary>text が無ければ丸いアイコンのボタン。style: primary・selected・danger。</summary>
    public static JsonObject Button(string id, string? text = null, string? icon = null, string? style = null, bool small = false) =>
        Set(new JsonObject { ["type"] = "button", ["id"] = id }, ("text", text), ("icon", icon), ("style", style), ("size", small ? "small" : null));

    public static JsonObject Icon(string name, string? color = null) => Set(new JsonObject { ["type"] = "icon", ["name"] = name }, ("color", color));
    public static JsonObject Divider() => new() { ["type"] = "divider" };
    public static JsonObject Spacer(float size = 0) => new() { ["type"] = "spacer", ["size"] = size };
    public static JsonObject Progress(double value, string? color = null) => Set(new JsonObject { ["type"] = "progress", ["value"] = value }, ("color", color));
    public static JsonObject Scroll(JsonNode child) => new() { ["type"] = "scroll", ["child"] = child };

    // --- 並べ方（どの部品にも付けられる） ---

    public static JsonObject Grow(this JsonObject o) => With(o, "grow", true);
    /// <summary>start・center・end。</summary>
    public static JsonObject Align(this JsonObject o, string align) => With(o, "align", align);
    public static JsonObject Gap(this JsonObject o, float gap) => With(o, "gap", gap);
    public static JsonObject Pad(this JsonObject o, float pad) => With(o, "pad", pad);
    public static JsonObject Pad(this JsonObject o, float x, float y) => With(With(o, "padX", x), "padY", y);
    /// <summary>panel・field・card・accent。</summary>
    public static JsonObject Background(this JsonObject o, string background, float radius = 18) => With(With(o, "background", background), "radius", radius);
    /// <summary>文の幅をこれより狭くしない（表の列をそろえる）。</summary>
    public static JsonObject Width(this JsonObject o, float width) => With(o, "width", width);
    /// <summary>文の寄せ方（start・center・end）。</summary>
    public static JsonObject TextAlign(this JsonObject o, string align) => With(o, "textAlign", align);

    static JsonObject With(JsonObject o, string key, JsonNode value)
    {
        o[key] = value;
        return o;
    }

    static JsonObject Set(JsonObject o, params (string Key, string? Value)[] values)
    {
        foreach (var (k, v) in values)
            if (v != null) o[k] = v;
        return o;
    }

    static JsonArray Children(JsonNode?[] children) => new(children.Where(c => c != null).Select(c => c!.Parent == null ? c : c.DeepClone()).ToArray());
}
