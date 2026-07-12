namespace TinyCs.Tests;

internal static class ConsoleCollection
{
    public const string Name = "Process-wide console";
}

[CollectionDefinition(ConsoleCollection.Name, DisableParallelization = true)]
public sealed class ConsoleCollectionDefinition
{
}
