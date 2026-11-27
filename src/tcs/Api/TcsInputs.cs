using System.Collections.Generic;

namespace Tcs;

// The input of a compile: sources to compile and references (declarations that are only type-checked, like --ref).
// The library shipped with tcs is added here, in one place.
public sealed class TcsInputs
{
    private static readonly string[] Library = "TinySystem/Linq.cs TinySystem/ValueTuple.cs".Split(' ');

    public List<string> Names = new List<string>();
    public List<string> Texts = new List<string>();
    public List<string> References = new List<string>();

    // name is the path shown in diagnostics ("" for none)
    public void Source(string name, string text)
    {
        Names.Add(name);
        Texts.Add(text);
    }

    public void Reference(string text)
    {
        References.Add(text);
    }

    // Reads the file and adds it as a source. false if it does not exist
    public bool SourceFile(string path)
    {
        if (!TcsHost.FileExists(path)) return false;
        Source(path, TcsHost.ReadFile(path));
        return true;
    }

    public bool ReferenceFile(string path)
    {
        if (!TcsHost.FileExists(path)) return false;
        Reference(TcsHost.ReadFile(path));
        return true;
    }

    public void AddTo(Session session)
    {
        for (int i = 0; i < Texts.Count; i++) session.Add(Names[i], Texts[i], 0);
        AddReferences(session, References);
    }

    // References and the library shipped with tcs (what a session needs besides its sources)
    public static void AddReferences(Session session, List<string> references)
    {
        foreach (var r in references) session.Add("", r, 1);
        foreach (var lib in Library) session.Add(lib, TcsHost.ToolFile("lib/" + lib), 2);
    }
}
