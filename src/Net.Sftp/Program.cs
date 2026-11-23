using Connector.Core.Cli;
using Connector.Core.Output;
using Net.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Net.Sftp;

internal static class Program
{
    private const string Usage =
        "用法:\n" +
        "  net-sftp ls   <user@host> [远端路径] [-l]\n" +
        "  net-sftp get  <user@host> <远端> <本地> [-r]\n" +
        "  net-sftp put  <user@host> <本地> <远端> [-r]\n" +
        "  net-sftp mkdir <user@host> <远端路径>\n" +
        "  net-sftp rm   <user@host> <远端路径> [-r]\n" +
        "\n" +
        "选项:\n" +
        "  -p, --port <n>        端口，默认 22\n" +
        "  -i, --identity <file> 私钥文件（默认探测 ~/.ssh）\n" +
        "      --password-stdin  从标准输入读一行密码\n" +
        "      --strict          主机不在 known_hosts 中时直接失败\n" +
        "      --insecure        跳过主机密钥校验\n" +
        "      --timeout <sec>   连接超时，默认 30\n" +
        "  -r, --recursive       目录递归（get / put / rm）\n" +
        "  -l, --long            长格式（ls）";

    private static readonly OptionDef[] Options =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-i", "--identity"),
        OptionDef.Flag("--password-stdin"),
        OptionDef.Flag("--strict"),
        OptionDef.Flag("--insecure"),
        OptionDef.Value("--timeout"),
        OptionDef.Flag("-r", "--recursive"),
        OptionDef.Flag("-l", "--long"),
    ];

    private static int Main(string[] args) => CliHost.Run(() => Run(args));

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            ConsoleOut.Line(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var verb = args[0];

        if (verb is not ("ls" or "get" or "put" or "mkdir" or "rm"))
        {
            ConsoleOut.Error($"未知子命令: {verb}");
            ConsoleOut.Error(Usage);
            return 1;
        }

        CliArgs parsed;

        try
        {
            parsed = CliArgs.Parse(args[1..], Options);
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            ConsoleOut.Error(Usage);
            return 1;
        }

        var positional = parsed.Positionals;

        if (positional.Count < 1)
        {
            ConsoleOut.Error(Usage);
            return 1;
        }

        RemoteTarget target;

        try
        {
            target = RemoteTarget.Parse(positional[0]);
        }
        catch (FormatException error)
        {
            ConsoleOut.Error(error.Message);
            return 1;
        }

        var options = new SshOptions
        {
            Target = target,
            Port = ParseInt(parsed.Get("-p"), 22, "端口"),
            IdentityPath = parsed.Get("-i"),
            Password = parsed.Has("--password-stdin") ? Console.In.ReadLine() : null,
            Strict = parsed.Has("--strict"),
            Insecure = parsed.Has("--insecure"),
            Timeout = TimeSpan.FromSeconds(ParseInt(parsed.Get("--timeout"), 30, "超时")),
        };

        var recursive = parsed.Has("-r");
        var longFormat = parsed.Has("-l");

        try
        {
            using var client = SshSession.OpenSftp(options);
            client.OperationTimeout = options.Timeout + TimeSpan.FromMinutes(10);

            return verb switch
            {
                "ls" => List(client, positional.Count > 1 ? positional[1] : ".", longFormat),
                "get" => Get(client, positional, recursive),
                "put" => Put(client, positional, recursive),
                "mkdir" => MakeDirectory(client, positional),
                "rm" => Remove(client, positional, recursive),
                _ => 1,
            };
        }
        catch (Exception error)
        {
            ConsoleOut.Error(CliHost.Describe(error));
            return 1;
        }
    }

    private static int List(SftpClient client, string path, bool longFormat)
    {
        var entries = client.ListDirectory(path)
            .Where(e => e.Name is not ("." or ".."))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var entry in entries)
        {
            var kind = entry.IsDirectory ? "d" : entry.IsSymbolicLink ? "l" : "-";

            if (longFormat)
            {
                ConsoleOut.Line(
                    $"{kind}{Perm(entry.OwnerCanRead, entry.OwnerCanWrite, entry.OwnerCanExecute)}" +
                    $"{Perm(entry.GroupCanRead, entry.GroupCanWrite, entry.GroupCanExecute)}" +
                    $"{Perm(entry.OthersCanRead, entry.OthersCanWrite, entry.OthersCanExecute)} " +
                    $"{entry.Length,12} {entry.LastWriteTimeUtc:yyyy-MM-dd HH:mm} {entry.Name}");
            }
            else
            {
                ConsoleOut.Line(entry.Name + (entry.IsDirectory ? "/" : string.Empty));
            }
        }

        return 0;
    }

    private static int Get(SftpClient client, IReadOnlyList<string> positional, bool recursive)
    {
        if (positional.Count < 3)
        {
            ConsoleOut.Error("用法: net-sftp get <user@host> <远端> <本地> [-r]");
            return 1;
        }

        var remote = positional[1];
        var local = positional[2];

        if (client.Exists(remote) && IsDirectory(client, remote))
        {
            if (!recursive)
            {
                ConsoleOut.Error($"错误: {remote} 是目录，下载目录需要 -r");
                return 1;
            }

            DownloadDirectory(client, remote, local);
            return 0;
        }

        var destination = Directory.Exists(local) ? Path.Combine(local, Path.GetFileName(remote)) : local;
        DownloadFile(client, remote, destination);
        return 0;
    }

    private static int Put(SftpClient client, IReadOnlyList<string> positional, bool recursive)
    {
        if (positional.Count < 3)
        {
            ConsoleOut.Error("用法: net-sftp put <user@host> <本地> <远端> [-r]");
            return 1;
        }

        var local = positional[1];
        var remote = positional[2];

        if (Directory.Exists(local))
        {
            if (!recursive)
            {
                ConsoleOut.Error($"错误: {local} 是目录，上传目录需要 -r");
                return 1;
            }

            UploadDirectory(client, local, remote);
            return 0;
        }

        if (!File.Exists(local))
        {
            ConsoleOut.Error($"错误: 本地文件不存在: {local}");
            return 1;
        }

        var destination = remote.EndsWith('/') || IsDirectory(client, remote)
            ? remote.TrimEnd('/') + "/" + Path.GetFileName(local)
            : remote;

        UploadFile(client, local, destination);
        return 0;
    }

    private static int MakeDirectory(SftpClient client, IReadOnlyList<string> positional)
    {
        if (positional.Count < 2)
        {
            ConsoleOut.Error("用法: net-sftp mkdir <user@host> <远端路径>");
            return 1;
        }

        var path = positional[1];

        if (client.Exists(path))
        {
            ConsoleOut.Error($"! 目录已存在: {path}");
            return 0;
        }

        client.CreateDirectory(path);
        ConsoleOut.Line($"✓ mkdir {path}");
        return 0;
    }

    private static int Remove(SftpClient client, IReadOnlyList<string> positional, bool recursive)
    {
        if (positional.Count < 2)
        {
            ConsoleOut.Error("用法: net-sftp rm <user@host> <远端路径> [-r]");
            return 1;
        }

        var path = positional[1];

        if (!client.Exists(path))
        {
            ConsoleOut.Error($"✗ 不存在: {path}");
            return 1;
        }

        if (IsDirectory(client, path))
        {
            if (!recursive)
            {
                ConsoleOut.Error($"错误: {path} 是目录，删除目录需要 -r");
                return 1;
            }

            RemoveDirectory(client, path);
            ConsoleOut.Line($"✓ rm -r {path}");
            return 0;
        }

        client.DeleteFile(path);
        ConsoleOut.Line($"✓ rm {path}");
        return 0;
    }

    private static bool IsDirectory(SftpClient client, string path)
    {
        try
        {
            return client.GetAttributes(path).IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    }

    private static void UploadFile(SftpClient client, string local, string remote)
    {
        using var stream = File.OpenRead(local);
        client.UploadFile(stream, remote, canOverride: true);
        ConsoleOut.Line($"✓ put {local} -> {remote} ({Human(stream.Length)})");
    }

    private static void UploadDirectory(SftpClient client, string localDir, string remoteDir)
    {
        EnsureRemoteDirectory(client, remoteDir);
        ConsoleOut.Line($"✓ mkdir {remoteDir}");

        foreach (var file in Directory.EnumerateFiles(localDir))
        {
            UploadFile(client, file, remoteDir.TrimEnd('/') + "/" + Path.GetFileName(file));
        }

        foreach (var dir in Directory.EnumerateDirectories(localDir))
        {
            UploadDirectory(client, dir, remoteDir.TrimEnd('/') + "/" + Path.GetFileName(dir));
        }
    }

    private static void DownloadFile(SftpClient client, string remote, string local)
    {
        var full = Path.GetFullPath(local);
        var dir = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var stream = File.Create(full))
        {
            client.DownloadFile(remote, stream);
            ConsoleOut.Line($"✓ get {remote} -> {local} ({Human(stream.Length)})");
        }
    }

    private static void DownloadDirectory(SftpClient client, string remoteDir, string localDir)
    {
        Directory.CreateDirectory(localDir);

        foreach (var entry in client.ListDirectory(remoteDir))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var localPath = Path.Combine(localDir, entry.Name);
            var remotePath = remoteDir.TrimEnd('/') + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                DownloadDirectory(client, remotePath, localPath);
            }
            else
            {
                DownloadFile(client, remotePath, localPath);
            }
        }
    }

    private static void RemoveDirectory(SftpClient client, string path)
    {
        foreach (var entry in client.ListDirectory(path))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var child = path.TrimEnd('/') + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                RemoveDirectory(client, child);
            }
            else
            {
                client.DeleteFile(child);
            }
        }

        client.DeleteDirectory(path);
    }

    private static void EnsureRemoteDirectory(SftpClient client, string path)
    {
        if (path.Length == 0 || path == "/" || client.Exists(path))
        {
            return;
        }

        client.CreateDirectory(path);
    }

    private static string Perm(bool read, bool write, bool execute) =>
        $"{(read ? 'r' : '-')}{(write ? 'w' : '-')}{(execute ? 'x' : '-')}";

    private static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:N2} MB",
    };

    private static int ParseInt(string? value, int fallback, string name)
    {
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new UsageException($"{name} 需要正整数，实际: {value}");
        }

        return parsed;
    }
}
