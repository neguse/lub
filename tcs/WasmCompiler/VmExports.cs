// Exposes the incremental VM session (doc/vm-design.md) to JS (V5). The playground worker receives the first module with Open,
// and on each edit receives from Update either a reload batch (the player applies it with tvm_apply_batch) or a restart (the module is reloaded).
// Modules and batches are base64 (the worker turns them back into ArrayBuffers and sends them to the player as transferables).
// Fixed to a single session. Each Open issues an epoch, and responses return the epoch (same rule as SessionExports).
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyCs.Vm;

public sealed class VmOpenRequest
{
    /// <summary>Input (path -> source).</summary>
    public Dictionary<string, string> Files { get; set; } = [];
    /// <summary>Host stubs (equivalent to --ref).</summary>
    public Dictionary<string, string>? Refs { get; set; }
    /// <summary>Equivalent to --entry (the class with Main).</summary>
    public string? EntryClass { get; set; }
    /// <summary>Equivalent to --export (`Game.*` etc.; functions the host looks up with tvm_find).</summary>
    public List<string>? Exports { get; set; }
}

public sealed class VmResponse
{
    public bool Ok { get; set; }
    /// <summary>module / batch / restart / error (VmSession.Result.Kind).</summary>
    public string Kind { get; set; } = "error";
    public int Epoch { get; set; }
    public int Revision { get; set; }
    /// <summary>module (base64). Set for module and restart.</summary>
    public string? Module { get; set; }
    /// <summary>reload batch (base64). Set for batch.</summary>
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
