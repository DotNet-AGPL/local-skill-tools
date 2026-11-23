using Connector.Core.Cli;
using System.Text;
using Renci.SshNet;

namespace Net.Core;

/// <summary>
/// 本地端口转发，等价于 ssh 的 <c>-L [bind_address:]port:host:hostport</c>。
/// 端口写 0 表示由系统分配，启动后可从 <see cref="ForwardedPortLocal.BoundPort"/> 读到实际端口。
/// </summary>
public sealed record LocalForward(string BoundHost, int BoundPort, string TargetHost, int TargetPort)
{
    public const string DefaultBoundHost = "127.0.0.1";

    public static bool TryParse(string text, out LocalForward forward, out string error)
    {
        forward = null!;
        error = string.Empty;

        var tokens = Split(text);

        if (tokens.Count == 3)
        {
            tokens.Insert(0, DefaultBoundHost);
        }

        if (tokens.Count != 4)
        {
            error = $"转发格式应为 [bind:]port:host:hostport，实际: {text}";
            return false;
        }

        var boundHost = tokens[0].Length == 0 ? DefaultBoundHost : tokens[0];

        if (boundHost == "*")
        {
            boundHost = "0.0.0.0";
        }

        if (!TryParsePort(tokens[1], out var boundPort))
        {
            error = $"本地端口无效: {tokens[1]}";
            return false;
        }

        if (!TryParsePort(tokens[3], out var targetPort) || targetPort == 0)
        {
            error = $"目标端口无效: {tokens[3]}";
            return false;
        }

        if (tokens[2].Length == 0)
        {
            error = $"缺少目标主机: {text}";
            return false;
        }

        forward = new LocalForward(boundHost, boundPort, tokens[2], targetPort);
        return true;
    }

    /// <summary>按冒号切分，但忽略方括号里的冒号（IPv6），括号本身不保留。</summary>
    private static List<string> Split(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inBracket = false;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '[':
                    inBracket = true;
                    continue;
                case ']':
                    inBracket = false;
                    continue;
                case ':' when !inBracket:
                    tokens.Add(current.ToString());
                    current.Clear();
                    continue;
                default:
                    current.Append(ch);
                    continue;
            }
        }

        tokens.Add(current.ToString());
        return tokens;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, out port) && port is >= 0 and <= 65535;

    public override string ToString() => $"{BoundHost}:{BoundPort} -> {TargetHost}:{TargetPort}";
}

public static class SshForwarding
{
    /// <summary>启动全部转发；任一失败则回滚已启动的端口并抛异常。</summary>
    public static List<ForwardedPortLocal> Start(SshClient client, IReadOnlyList<LocalForward> forwards)
    {
        var started = new List<ForwardedPortLocal>();

        foreach (var forward in forwards)
        {
            var port = new ForwardedPortLocal(
                forward.BoundHost,
                (uint)forward.BoundPort,
                forward.TargetHost,
                (uint)forward.TargetPort);

            client.AddForwardedPort(port);

            try
            {
                port.Start();
            }
            catch (Exception error)
            {
                Stop(client, started);
                throw new UserErrorException($"无法建立转发 {forward}：{error.Message}", error);
            }

            started.Add(port);
        }

        return started;
    }

    public static void Stop(SshClient client, IEnumerable<ForwardedPortLocal> ports)
    {
        foreach (var port in ports)
        {
            try
            {
                if (port.IsStarted)
                {
                    port.Stop();
                }

                client.RemoveForwardedPort(port);
            }
            catch
            {
                // 关闭阶段的异常不覆盖正常退出码
            }
        }
    }
}
