namespace TinyCs;

// --watch --reload (doc/unified-reload-design.md §4.3)。常駐の IncrementalCompilationSession に
// 変わった file だけを渡し、成功した build ごとに -o へ全部の module snapshot (起動と restart 用)
// を書いてから、host の VM が動かしている revision へ当てる batch (変わった module と instance の
// 移行) を @@tcs_reload_chunk で送る。restart の要る編集は @@tcs_restart_required、compile error は
// @@tcs_build_failed (VM の revision はそのまま)
internal sealed class ModuleWatch(List<string> inputPaths, List<string> refPaths,
    string outputPath, string entryClass, IReadOnlyList<string> tracePrefixes,
    bool warningsAsErrors)
{
    private IncrementalCompilationSession? _session;
    private string[] _refTexts = [];
    // session に最後に渡した file の中身
    private readonly Dictionary<string, string> _texts = [];
    // host の VM が動かしている revision (0 は出力をまだ書いていない)
    private int _running;
    // _running から変わった module と、restart の理由
    private readonly HashSet<string> _changed = [];
    private readonly List<string> _restartReasons = [];

    public void Rebuild()
    {
        try
        {
            var refs = refPaths.Select(File.ReadAllText).ToArray();
            var texts = inputPaths.ToDictionary(p => p, File.ReadAllText);
            if (_session == null || !refs.SequenceEqual(_refTexts))
            {
                if (_session != null)
                    _restartReasons.Add("reference source changed");
                Open(texts, refs);
            }
            else
            {
                var changed = inputPaths.Where(p => texts[p] != _texts[p]).ToList();
                if (changed.Count == 0)
                    return;
                foreach (var path in changed)
                {
                    var result = _session.Update(path, texts[path]);
                    _texts[path] = texts[path];
                    if (!result.Success)
                        continue;
                    foreach (var artifact in result.ChangedArtifacts)
                        _changed.Add(artifact.ModuleId);
                    _restartReasons.AddRange(result.RestartReasons);
                }
            }
            Publish();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}");
        }
    }

    private void Open(Dictionary<string, string> texts, string[] refs)
    {
        _session = new IncrementalCompilationSession(refs.Length > 0 ? refs : null,
            instanceRegistry: true, tracePrefixes: tracePrefixes);
        _session.OpenProject([.. inputPaths.Select(p => (p, texts[p]))]);
        _refTexts = refs;
        _texts.Clear();
        foreach (var (path, text) in texts)
            _texts[path] = text;
        // 新しい session は前の session の revision を知らないので、全部の module を当て直す
        _changed.UnionWith(_session.Artifacts.Select(a => a.ModuleId));
    }

    private void Publish()
    {
        var session = _session!;
        var (errors, warnings) = session.CollectDiagnostics();
        foreach (var warn in warnings)
            Console.Error.WriteLine($"  {warn}");
        if (errors.Count == 0 && session.HasTopLevelStatements)
            errors = ["--reload: top-level statements are not supported (move them into the entry class)"];
        if (errors.Count == 0 && warningsAsErrors && warnings.Count > 0)
            errors = warnings;
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Build FAILED:");
            foreach (var err in errors)
                Console.Error.WriteLine($"  {err}");
            if (_running > 0)
                Program.WriteHostMessage("build_failed", string.Concat(errors.Select(e => e + "\n")));
            return;
        }

        var revision = session.Revision;
        OutputFileWriter.WriteAllText(outputPath, ModuleLinker.LinkSnapshot(session.Artifacts,
            revision, entryClass, LuaRuntime.LoadTinySystemSource(),
            LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath)));
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Built {outputPath} (revision {revision})");

        // 出力を書いてから送る: その間につないだ host は出力 (この revision) を読む
        if (_running > 0)
        {
            string? migration = null;
            if (_restartReasons.Count == 0)
            {
                try
                {
                    migration = session.MigrationFrom(_running);
                }
                catch (InvalidOperationException ex)
                {
                    _restartReasons.Add(ex.Message);
                }
            }
            if (_restartReasons.Count > 0)
            {
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Restart required "
                    + $"(restart the host to run revision {revision}):");
                foreach (var reason in _restartReasons)
                    Console.Error.WriteLine($"  {reason}");
                Program.WriteHostMessage("restart_required",
                    string.Concat(_restartReasons.Select(r => r + "\n")));
            }
            else
            {
                Program.WriteHostMessage("reload_chunk", ModuleLinker.LinkBatch(session.Artifacts,
                    _changed, revision, _running, entryClass, migration: migration));
                Console.Error.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Sent reload batch {_running} -> {revision}");
            }
        }
        _running = revision;
        _changed.Clear();
        _restartReasons.Clear();
    }
}
