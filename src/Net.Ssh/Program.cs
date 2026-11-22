using System.Runtime.InteropServices;
using System.Text;
using Connector.Core.Cli;
using Connector.Core.Output;
using Net.Core;
using Renci.SshNet;

namespace Net.Ssh;

internal static class Program
{
    private const string Usage =
        "用法: net-ssh exec <user@host> [选项] -- <命令>\n" +
        "      net-ssh exec <user@host> [选项] -f script.sh\n" +
        "      net-ssh tunnel <user@host> [选项] -L [bind:]port:host:hostport [-L ...]\n" +
        "\n" +
        "选项:\n" +
        "  -p, --port <n>        端口，默认 22\n" +
        "  -i, --identity <file> 私钥文件（默认探测 ~/.ssh/id_ed25519|id_ecdsa|id_rsa）\n" +
        "      --password-stdin  从标准输入读一行密码\n" +
        "      --strict          主机不在 known_hosts 中时直接失败\n" +
        "      --insecure        跳过主机密钥校验（等价 ssh StrictHostKeyChecking=no）\n" +
        "      --timeout <sec>   连接超时，默认 30\n" +
        "  -L, --forward <spec>  本地端口转发，可重复；端口写 0 由系统分配\n" +
        "      --duration <sec>  tunnel 存活时长，0 表示一直保持（默认 0）\n" +
        "  -f, --file <file>     从本地文件读取要执行的命令（仅 exec）";

    private static readonly OptionDef[] Options =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-i", "--identity"),
        OptionDef.Flag("--password-stdin"),
        OptionDef.Flag("--strict"),
        OptionDef.Flag("--insecure"),
        OptionDef.Value("--timeout"),
        OptionDef.Value("-L", "--forward"),
        OptionDef.Value("--duration"),
        OptionDef.Value("-f", "--file"),
    ];

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        finally
        {
            ConsoleOut.Flush();
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            ConsoleOut.Line(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var rest = args[1..];

        return args[0] switch
        {
            "exec" => Exec(rest),
            "tunnel" => Tunnel(rest),
            _ => UnknownCommand(args[0]),
        };
    }

    private static int UnknownCommand(string command)
    {
        ConsoleOut.Error($"未知子命令: {command}");
        ConsoleOut.Error(Usage);
        return 1;
    }

    private static int Exec(string[] rest)
    {
        // "--" 之后的内容原样作为远端命令，避免把 "ls -l" 里的 -l 当成自己的选项
        var separator = Array.IndexOf(rest, "--");
        var remoteArgs = separator >= 0 ? rest[(separator + 1)..] : [];
        var optionArgs = separator >= 0 ? rest[..separator] : rest;

        if (!TryParse(optionArgs, out var parsed, out _, out var options))
        {
            return 1;
        }

        var forwards = ParseForwards(parsed);
        if (forwards is null)
        {
            return 1;
        }

        string command;

        if (parsed.Get("-f") is { } scriptPath)
        {
            if (!File.Exists(scriptPath))
            {
                ConsoleOut.Error($"错误: 脚本文件不存在: {scriptPath}");
                return 1;
            }

            command = File.ReadAllText(scriptPath);
        }
        else
        {
            if (remoteArgs.Length == 0)
            {
                ConsoleOut.Error("错误: 缺少要执行的命令（用 -- 分隔，例如: net-ssh exec root@host -- ls -l）");
                return 1;
            }

            command = string.Join(' ', remoteArgs);
        }

        if (command.Trim().Length == 0)
        {
            ConsoleOut.Error("错误: 命令为空");
            return 1;
        }

        try
        {
            using var client = SshSession.Open(options);
            var ports = SshForwarding.Start(client, forwards);

            try
            {
                PrintForwards(ports);

                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = options.Timeout + TimeSpan.FromMinutes(10);

                var stdout = cmd.Execute();
                var stderr = cmd.Error;

                WriteRaw(Console.OpenStandardOutput(), stdout);

                if (!string.IsNullOrEmpty(stderr))
                {
                    WriteRaw(Console.OpenStandardError(), stderr);
                }

                return cmd.ExitStatus ?? 0;
            }
            finally
            {
                SshForwarding.Stop(client, ports);
            }
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int Tunnel(string[] rest)
    {
        if (!TryParse(rest, out var parsed, out _, out var options))
        {
            return 1;
        }

        var forwards = ParseForwards(parsed);

        if (forwards is null)
        {
            return 1;
        }

        if (forwards.Count == 0)
        {
            ConsoleOut.Error("错误: tunnel 至少需要一个 -L [bind:]port:host:hostport");
            return 1;
        }

        int duration;

        try
        {
            duration = ParseInt(parsed.Get("--duration"), 0, "存活时长", allowZero: true);
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            return 1;
        }

        try
        {
            using var client = SshSession.Open(options);
            var ports = SshForwarding.Start(client, forwards);

            try
            {
                PrintForwards(ports);
                ConsoleOut.Info("按 Ctrl+C 结束隧道" + (duration > 0 ? $"（{duration} 秒后自动关闭）" : string.Empty));

                using var stop = new ManualResetEventSlim();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    stop.Set();
                };

                void WaitForStop()
                {
                    if (duration > 0)
                    {
                        stop.Wait(TimeSpan.FromSeconds(duration));
                    }
                    else
                    {
                        stop.Wait();
                    }
                }

                if (OperatingSystem.IsWindows())
                {
                    WaitForStop();
                }
                else
                {
                    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                    {
                        context.Cancel = true;
                        stop.Set();
                    });

                    WaitForStop();
                }
                ConsoleOut.Info("隧道已关闭");
                return 0;
            }
            finally
            {
                SshForwarding.Stop(client, ports);
            }
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static bool TryParse(
        string[] args,
        out CliArgs parsed,
        out RemoteTarget target,
        out SshOptions options)
    {
        parsed = null!;
        target = null!;
        options = null!;

        try
        {
            parsed = CliArgs.Parse(args, Options);
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            ConsoleOut.Error(Usage);
            return false;
        }

        if (parsed.Positionals.Count < 1)
        {
            ConsoleOut.Error(Usage);
            return false;
        }

        try
        {
            target = RemoteTarget.Parse(parsed.Positionals[0]);
        }
        catch (FormatException error)
        {
            ConsoleOut.Error(error.Message);
            return false;
        }

        try
        {
            options = new SshOptions
            {
                Target = target,
                Port = ParseInt(parsed.Get("-p"), 22, "端口"),
                IdentityPath = parsed.Get("-i"),
                Password = parsed.Has("--password-stdin") ? Console.In.ReadLine() : null,
                Strict = parsed.Has("--strict"),
                Insecure = parsed.Has("--insecure"),
                Timeout = TimeSpan.FromSeconds(ParseInt(parsed.Get("--timeout"), 30, "超时")),
            };
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            return false;
        }

        return true;
    }

    private static List<LocalForward>? ParseForwards(CliArgs parsed)
    {
        var forwards = new List<LocalForward>();

        foreach (var spec in parsed.GetAll("-L"))
        {
            if (!LocalForward.TryParse(spec, out var forward, out var error))
            {
                ConsoleOut.Error($"错误: {error}");
                return null;
            }

            forwards.Add(forward);
        }

        return forwards;
    }

    private static void PrintForwards(IEnumerable<ForwardedPortLocal> ports)
    {
        foreach (var port in ports)
        {
            ConsoleOut.Info($"listening {port.BoundHost}:{port.BoundPort} -> {port.Host}:{port.Port}");
        }
    }

    private static void WriteRaw(Stream stream, string text)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(text);
        writer.Flush();
    }

    private static int ParseInt(string? value, int fallback, string name, bool allowZero = false)
    {
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed) || parsed < (allowZero ? 0 : 1))
        {
            throw new UsageException($"{name} 需要{(allowZero ? "非负" : "正")}整数，实际: {value}");
        }

        return parsed;
    }
}
