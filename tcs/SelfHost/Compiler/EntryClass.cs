using System.Collections.Generic;

namespace TcsSelf;

// entry class (--entry) の解決。namespace 修飾名か、compile の中で一意な simple 名で引く
public static class EntryClass
{
    // 見つからない、曖昧、--ref の型、class でない、のときは errors に理由を足して null を返す
    public static TypeDeclSym? Resolve(Compilation c, string entry, List<string> errors)
    {
        TypeDeclSym? found = null;
        if (c.TypesByFullName.TryGetValue(entry, out var byFull)) found = byFull;
        if (found == null)
        {
            var candidates = new List<TypeDeclSym>();
            foreach (var t in c.Types)
            {
                if (t.Name == entry) candidates.Add(t);
            }
            if (candidates.Count > 1)
            {
                var names = new List<string>();
                foreach (var t in candidates) names.Add(t.FullName);
                errors.Add("entry class is ambiguous: " + entry + " (" + string.Join(", ", names) + ")");
                return null;
            }
            if (candidates.Count == 1) found = candidates[0];
        }
        if (found == null)
        {
            errors.Add("entry class not found: " + entry);
            return null;
        }
        if (found.IsRef)
        {
            errors.Add("entry class is reference-only (--ref): " + entry);
            return null;
        }
        if (found.DeclKind != DeclKinds.Class && found.DeclKind != DeclKinds.RecordClass)
        {
            errors.Add("entry class must be a class or record class: " + found.FullName);
            return null;
        }
        return found;
    }
}
