using System.Collections.Generic;

namespace Tcs;

// Resolution of the entry class (--entry). Looked up by namespace-qualified name or by a simple name unique within the compile
public static class EntryClass
{
    // If not found, ambiguous, a --ref type, or not a class, add the reason to errors and return null
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
