using System.Collections.Generic;

namespace TcsSelf;

// Context for building IL. The IL consumer (IlExporter) sets it each time it enters a type and member, and IlBuild reads it
public sealed class IlContext
{
    public readonly IlBuild B;
    // The path that passes IL to the C backend keeps the argument copy points (C passes by value and needs no copy)
    public bool KeepsArgumentCopies;
    // The type being built
    public TypeDeclSym? CurrentDecl;
    // Instance members of a generic type read type-argument defaults from self
    public bool TypeArgDefaultsOnSelf;
    // The method being built (used to check whether the expression body is void)
    public Symbol? CurrentFunction;
    // Whether the return of the lambda being built is void (0 = outside a lambda, 1 = void, 2 = value)
    public int CurrentLambdaVoid;
    // Record of shapes that could not be turned into IL (for investigation)
    public List<string> InternalErrors = new List<string>();

    public IlContext()
    {
        B = new IlBuild(this);
    }

    public void Internal(string message)
    {
        InternalErrors.Add(message);
    }
}
