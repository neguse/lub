namespace TinyCs.Tests;

// tcs --watch --reload の標準出力 (`@@tcs_<種類> <byte 数>` の行 + 本文の組) を読む
internal sealed class WatchMessageReader
{
    private const string Prefix = "@@tcs_";
    private const string Malformed = "<malformed header>";
    private readonly System.Collections.Concurrent.BlockingCollection<(string Kind, string Body)>
        _messages = new();

    public WatchMessageReader(Stream stdout)
    {
        new Thread(() =>
        {
            try
            {
                while (ReadLine(stdout) is { } line)
                {
                    if (!line.StartsWith(Prefix, StringComparison.Ordinal))
                        continue;
                    var space = line.LastIndexOf(' ');
                    if (space < 0 || !int.TryParse(line[(space + 1)..], out var length))
                    {
                        _messages.Add((Malformed, line));
                        return;
                    }
                    var body = new byte[length];
                    stdout.ReadExactly(body);
                    _messages.Add((line[Prefix.Length..space],
                        System.Text.Encoding.UTF8.GetString(body)));
                }
            }
            catch (IOException) { }
            finally { _messages.CompleteAdding(); }
        }) { IsBackground = true }.Start();
    }

    // 次の message の本文。種類が違えば失敗する
    public string Next(string kind, int timeoutMs)
    {
        if (!_messages.TryTake(out var message, timeoutMs))
            throw new TimeoutException($"no @@tcs_{kind} within {timeoutMs}ms");
        Assert.True(message.Kind != Malformed,
            $"expected @@tcs_{kind}, got a malformed header: {message.Body}");
        Assert.True(message.Kind == kind,
            $"expected @@tcs_{kind}, got @@tcs_{message.Kind}:\n{message.Body}");
        return message.Body;
    }

    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        for (var b = stream.ReadByte(); b >= 0; b = stream.ReadByte())
        {
            if (b == '\n') return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add((byte)b);
        }
        return null;
    }
}
