using HostApi;

public class HostApiSample
{
    public static string DescribeFrame()
    {
        Log.Info("describe-frame");
        return $"screen={Screen.Width()}x{Screen.Height()} dt={Time.DeltaSeconds():F2}";
    }
}
