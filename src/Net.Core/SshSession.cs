using Connector.Core.Cli;
using Connector.Core.Output;
using Renci.SshNet;

namespace Net.Core;

/// <summary>连接参数（由各 CLI 从 argv 解析后填入）。</summary>
public sealed class SshOptions
{
    public required RemoteTarget Target { get; init; }

    public int Port { get; init; } = 22;

    public string? IdentityPath { get; init; }

    public string? Password { get; init; }

    /// <summary>完全不校验主机密钥（等价于 ssh -o StrictHostKeyChecking=no）。</summary>
    public bool Insecure { get; init; }

    /// <summary>主机不在 known_hosts 时直接失败，而不是只提示。</summary>
    public bool Strict { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public static class SshSession
{
    private static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    public static SshClient Open(SshOptions options)
    {
        var client = new SshClient(CreateConnectionInfo(options));
        AttachHostKeyHandler(client, options);
        client.Connect();
        return client;
    }

    public static SftpClient OpenSftp(SshOptions options)
    {
        var client = new SftpClient(CreateConnectionInfo(options));
        AttachHostKeyHandler(client, options);
        client.Connect();
        return client;
    }

    public static ConnectionInfo CreateConnectionInfo(SshOptions options)
    {
        var methods = BuildAuthMethods(options);

        if (methods.Count == 0)
        {
            throw new UserErrorException(
                "没有可用的认证方式：请用 -i <私钥> 指定密钥，或用 --password-stdin 从标准输入读密码");
        }

        return new ConnectionInfo(options.Target.Host, options.Port, options.Target.User, [.. methods])
        {
            Timeout = options.Timeout,
        };
    }

    /// <summary>主机密钥校验挂在 BaseClient 上（SSH.NET 2025+ 已从 ConnectionInfo 移除）。</summary>
    private static void AttachHostKeyHandler(BaseClient client, SshOptions options)
    {
        var warned = false;

        client.HostKeyReceived += (_, e) =>
        {
            if (options.Insecure)
            {
                e.CanTrust = true;

                if (!warned)
                {
                    ConsoleOut.Error($"! 已跳过主机密钥校验（{e.HostKeyName} {KnownHosts.Fingerprint(e.HostKey)}）");
                    warned = true;
                }

                return;
            }

            var hostName = options.Target.KnownHostsName(options.Port);

            switch (KnownHosts.Verify(hostName, e.HostKeyName, e.HostKey, out var detail))
            {
                case KnownHosts.CheckResult.Match:
                    e.CanTrust = true;
                    return;

                case KnownHosts.CheckResult.Mismatch:
                    ConsoleOut.Error($"✗ {detail}");
                    ConsoleOut.Error("  可能是中间人攻击，或服务器更换了主机密钥；确认无误后从 known_hosts 删除对应行。");
                    e.CanTrust = false;
                    return;

                case KnownHosts.CheckResult.Skipped:
                    if (options.Strict)
                    {
                        ConsoleOut.Error($"✗ {detail}");
                        e.CanTrust = false;
                        return;
                    }

                    ConsoleOut.Error($"! {detail}");
                    e.CanTrust = true;
                    return;

                default:
                    if (options.Strict)
                    {
                        ConsoleOut.Error($"✗ {hostName} 不在 known_hosts 中（--strict）");
                        e.CanTrust = false;
                        return;
                    }

                    if (!warned)
                    {
                        ConsoleOut.Error(
                            $"! 首次连接 {hostName}：{e.HostKeyName} {KnownHosts.Fingerprint(e.HostKey)}（未写入 known_hosts，可用 --strict 强制校验）");
                        warned = true;
                    }

                    e.CanTrust = true;
                    return;
            }
        };
    }

    private static List<AuthenticationMethod> BuildAuthMethods(SshOptions options)
    {
        var methods = new List<AuthenticationMethod>();
        var identity = options.IdentityPath;

        if (identity is null)
        {
            var sshDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh");

            foreach (var name in DefaultKeyNames)
            {
                var candidate = Path.Combine(sshDir, name);

                if (File.Exists(candidate))
                {
                    identity = candidate;
                    break;
                }
            }
        }

        if (identity is not null)
        {
            var keyFile = new PrivateKeyFile(identity);
            methods.Add(new PrivateKeyAuthenticationMethod(options.Target.User, keyFile));
        }

        if (!string.IsNullOrEmpty(options.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(options.Target.User, options.Password));
        }

        return methods;
    }
}
