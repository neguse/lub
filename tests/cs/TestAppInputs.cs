// App.Inputs: ゲームの .csproj から、変換の入力を集める (lub が .csproj を開くときと同じ規則)。
// web は .csproj を読めないので null。
using System;
using Lub;

public static class TestAppInputs
{
    public static void OnFrame(float dt)
    {
        var inputs = App.Inputs("tests/cs/TestAppInputs.csproj");
        if (Sys.IsWeb())
        {
            Check.That(inputs == null, "test_app_inputs: the web player cannot read a .csproj");
            Check.Pass();
            return;
        }
        Check.That(inputs != null, "test_app_inputs: no inputs for tests/cs/TestAppInputs.csproj");
        Check.That(inputs!.Entry == "TestAppInputs", "test_app_inputs: entry " + inputs.Entry);
        Check.That(inputs.Sources.Contains("/tests/cs/TestAppInputs.cs\n"), "test_app_inputs: the test file is not a source");
        Check.That(inputs.Sources.Contains("/cs-lib/") && !inputs.Sources.Contains("lub_stub.cs"),
            "test_app_inputs: the sources should have cs-lib without the stub");
        Check.That(inputs.Refs.Contains("/cs-lib/lub_stub.cs\n"), "test_app_inputs: the stub is not a ref");
        Check.That(inputs.Dirs.Contains("/tests/cs\n") && !inputs.Dirs.Contains("cs-lib"),
            "test_app_inputs: dirs " + inputs.Dirs);
        Check.That(App.Inputs("tests/cs/Missing.csproj") == null, "test_app_inputs: a missing .csproj has inputs");
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
