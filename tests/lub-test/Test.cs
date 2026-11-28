namespace LubTest;

/// <summary>One test: a name, the labels it is selected by, and what it does. The body throws TestFailed (Run.Fail, Run.Check) to fail.</summary>
public sealed class Test(string name, string[] labels, Func<Run, Task> body)
{
    public string Name { get; } = name;
    public string[] Labels { get; } = labels;
    public Func<Run, Task> Body { get; } = body;
    /// <summary>The whole test is stopped and fails after this long.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>How many of the job slots it takes (a test that itself uses every core takes them all).</summary>
    public bool Alone { get; init; }
}

public sealed class TestFailed(string message) : Exception(message);
