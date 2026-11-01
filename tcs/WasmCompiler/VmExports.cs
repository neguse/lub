// VM (doc/vm-design.md) の増分 session を JS へ公開する (V5)。playground の worker は Open で最初の module を受け取り、
// 編集ごとに Update で reload の batch (player が tvm_apply_batch で当てる) か restart (module を読み直す) を受け取る。
// module と batch は base64 (worker が ArrayBuffer に戻して transferable で player へ送る)。
// single session 固定。Open のたびに epoch を発行し、応答は epoch を返す (SessionExports と同じ約束)。
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyCs.Vm;

public sealed class VmOpenRequest
{
    /// <summary>入力 (path -> source)。</summary>
    public Dictionary<string, string> Files { get; set; } = [];
    /// <summary>host の stub (--ref 相当)。</summary>
    public Dictionary<string, string>? Refs { get; set; }
    /// <summary>--entry 相当 (Main を持つ class)。</summary>
    public string? EntryClass { get; set; }
    /// <summary>--export 相当 (`Game.*` など。host が tvm_find で引く関数)。</summary>
    public List<string>? Exports { get; set; }
}

public sealed class VmResponse
{
    public bool Ok { get; set; }
    /// <summary>module / batch / restart / error (VmSession.Result.Kind)。</summary>
    public string Kind { get; set; } = "error";
    public int Epoch { get; set; }
    public int Revision { get; set; }
    /// <summary>module (base64)。module と restart のとき。</summary>
    public string? Module { get; set; }
    /// <summary>reload の batch (base64)。batch のとき。</summary>
    public string? Batch { get; set; }
    public List<string> Reasons { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VmOpenRequest))]
[JsonSerializable(typeof(VmResponse))]
public sealed partial class VmJsonContext : JsonSerializerContext;

public static partial class VmExports
{
    private static VmSession? _session;
    private static readonly SortedDictionary<string, string> Files = new(StringComparer.Ordinal);
    private static int _epoch;
    private static int _revision;

    [JSExport]
    public static string Open(string requestJson)
    {
        try
        {
            CompilerExports.EnsureInit();
            var req = JsonSerializer.Deserialize(requestJson, VmJsonContext.Default.VmOpenRequest)
                      ?? throw new InvalidOperationException("empty request");
            _epoch++;
            _revision = 0;
            Files.Clear();
            foreach (var (path, source) in req.Files) Files[path] = source;
            _session = new VmSession(req.EntryClass, req.Exports ?? [], [.. (req.Refs ?? []).Values]);
            return Respond(_session.Update([.. Files.Values], [.. Files.Keys]));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [JSExport]
    public static string Update(int epoch, string path, string content)
    {
        try
        {
            if (_session is null || epoch != _epoch)
                throw new InvalidOperationException($"stale epoch {epoch} (current {_epoch})");
            Files[path] = content;
            return Respond(_session.Update([.. Files.Values], [.. Files.Keys]));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    private static string Respond(VmSession.Result r)
    {
        if (r.Kind != "error") _revision++;
        return JsonSerializer.Serialize(new VmResponse
        {
            Ok = r.Kind != "error",
            Kind = r.Kind,
            Epoch = _epoch,
            Revision = _revision,
            Module = r.Module is null ? null : Convert.ToBase64String(r.Module),
            Batch = r.Batch is null ? null : Convert.ToBase64String(r.Batch),
            Reasons = [.. r.Reasons ?? []],
            Errors = [.. r.Errors ?? []],
        }, VmJsonContext.Default.VmResponse);
    }

    private static string Error(Exception ex) =>
        JsonSerializer.Serialize(new VmResponse { Ok = false, Kind = "error", Epoch = _epoch, Errors = [ex.ToString()] },
            VmJsonContext.Default.VmResponse);
}
