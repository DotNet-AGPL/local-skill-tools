using System.Diagnostics;
using System.Globalization;
using System.Text;
using Connector.Core.Cli;
using Connector.Core.Output;
using StackExchange.Redis;

return CliHost.Run(() => RedisCli.Run(args));

internal static class RedisCli
{
    private const string QueryUsage =
        "用法: db-redis query <host> -c \"COMMAND arg1 arg2\" [-p port] [--password pw] [--user u] [-d db] [--secure]";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("--password"),
        OptionDef.Value("--user"),
        OptionDef.Value("-d", "--db"),
        OptionDef.Value("-c", "--command"),
        OptionDef.Flag("--secure"),
    ];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-redis connect <host> [-p port] [--password pw] [--user u] [-d db] [--secure]");
            return 1;
        }

        var command = args[0];
        var rest = args[1..];

        return command switch
        {
            "query" => Query(rest),
            "connect" => Connect(rest),
            _ => UnknownCommand(command),
        };
    }

    private static int UnknownCommand(string command)
    {
        ConsoleOut.Error($"未知子命令: {command}");
        ConsoleOut.Error(QueryUsage);
        return 1;
    }

    private static int Query(string[] args)
    {
        CliArgs parsed;

        try
        {
            parsed = CliArgs.Parse(args, QueryOptions);
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            ConsoleOut.Error(QueryUsage);
            return 1;
        }

        if (parsed.Positionals.Count < 1)
        {
            ConsoleOut.Error(QueryUsage);
            return 1;
        }

        var text = parsed.Get("-c") ?? string.Empty;

        if (text.Trim().Length == 0)
        {
            ConsoleOut.Error("错误: 请提供 Redis 命令 (-c)");
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);
        var tokens = Tokenize(text);
        var name = tokens[0].ToUpperInvariant();
        var arguments = tokens.Skip(1).ToArray();

        try
        {
            using var multiplexer = Connect(endpoint);
            var database = multiplexer.GetDatabase(endpoint.Database);

            ConsoleOut.Info($"正在执行命令: {name} {string.Join(' ', arguments)}");

            var stopwatch = Stopwatch.StartNew();
            var result = database.Execute(name, arguments);
            stopwatch.Stop();

            ConsoleOut.Info(string.Empty);
            ConsoleOut.Info($"命令执行完成 ({stopwatch.ElapsedMilliseconds}ms)");
            ConsoleOut.Info(string.Empty);
            ConsoleOut.Info("--- 结果 ---");
            ConsoleOut.Info(string.Empty);
            WriteResult(result);
            ConsoleOut.Info(string.Empty);
            ConsoleOut.Info("✓ 命令执行成功");
            return 0;
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ 命令执行失败: {error.Message}");
            return 1;
        }
    }

    /// <summary>
    /// RESP 结果到可读文本：字符串原样输出（与 Node 版一致），数组/集合走 JSON 数组，
    /// Map 走 JSON 对象，nil 输出 (nil)。
    /// </summary>
    private static void WriteResult(RedisResult result)
    {
        switch (result.Resp3Type)
        {
            case ResultType.Null:
                ConsoleOut.Line("(nil)");
                return;
            case ResultType.Integer:
                ConsoleOut.Line(((long)result).ToString(CultureInfo.InvariantCulture));
                return;
            case ResultType.BigInteger:
                ConsoleOut.Line(result.ToString());
                return;
            case ResultType.Double:
                ConsoleOut.Line(ValueFormat.FormatDouble((double)result));
                return;
            case ResultType.Boolean:
                ConsoleOut.Line((bool)result ? "true" : "false");
                return;
            case ResultType.SimpleString:
            case ResultType.BulkString:
            case ResultType.VerbatimString:
                ConsoleOut.Line((string)result!);
                return;
            case ResultType.Array:
            case ResultType.Set:
            {
                var items = new List<object?>();

                for (var i = 0; i < result.Length; i++)
                {
                    items.Add(Convert(result[i]));
                }

                ConsoleOut.Block(JsonText.Value(items));
                return;
            }
            case ResultType.Map:
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var pair in result.ToDictionary(null))
                {
                    map[pair.Key] = Convert(pair.Value);
                }

                ConsoleOut.Block(JsonText.Value(map));
                return;
            }
            default:
                ConsoleOut.Line(result.ToString());
                return;
        }
    }

    private static object? Convert(RedisResult result) => result.Resp3Type switch
    {
        ResultType.Null => null,
        ResultType.Integer => (long)result,
        ResultType.BigInteger => result.ToString(),
        ResultType.Double => (double)result,
        ResultType.Boolean => (bool)result,
        ResultType.SimpleString or ResultType.BulkString or ResultType.VerbatimString => (string)result!,
        ResultType.Array or ResultType.Set => ConvertArray(result),
        ResultType.Map => ConvertMap(result),
        _ => result.ToString(),
    };

    private static object?[] ConvertArray(RedisResult result)
    {
        var items = new object?[result.Length];

        for (var i = 0; i < result.Length; i++)
        {
            items[i] = Convert(result[i]);
        }

        return items;
    }

    private static Dictionary<string, object?> ConvertMap(RedisResult result)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var pair in result.ToDictionary(null))
        {
            map[pair.Key] = Convert(pair.Value);
        }

        return map;
    }

    /// <summary>
    /// 命令切分：支持单/双引号，便于传带空格的值。Node 版是裸按空白切分，
    /// 这里是有意改进（引号内的空格不再被拆开）。
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var character in text)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        if (tokens.Count == 0)
        {
            throw new UsageException("命令为空");
        }

        return tokens;
    }

    private static int Connect(string[] args)
    {
        if (args.Length < 1 || args[0].StartsWith('-'))
        {
            ConsoleOut.Error("用法: db-redis connect <host> [-p port] [--password pw] [--user u] [-d db] [--secure]");
            ConsoleOut.Error("示例: db-redis connect localhost -p 6379 -d 0");
            return 1;
        }

        CliArgs parsed;

        try
        {
            parsed = CliArgs.Parse(args, QueryOptions);
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            return 1;
        }

        if (parsed.Positionals.Count < 1)
        {
            ConsoleOut.Error("用法: db-redis connect <host> [-p port] [--password pw] [--user u] [-d db] [--secure]");
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);

        try
        {
            ConsoleOut.Line($"正在连接到 Redis 服务器: {endpoint.Host}:{endpoint.Port}...");

            using var multiplexer = Connect(endpoint);
            var database = multiplexer.GetDatabase(endpoint.Database);

            var pong = database.Execute("PING");
            ConsoleOut.Line("✓ 连接成功！");
            ConsoleOut.Line($"PING 响应: {(string)pong!}");

            var info = (string)database.Execute("INFO", "server")!;

            var version = Match(info, "redis_version");
            if (version.Length > 0)
            {
                ConsoleOut.Line($"Redis 版本: {version}");
            }

            var mode = Match(info, "redis_mode");
            if (mode.Length > 0)
            {
                ConsoleOut.Line($"运行模式: {mode}");
            }

            var size = (long)database.Execute("DBSIZE");
            ConsoleOut.Line($"当前数据库 (db{endpoint.Database}) 键数量: {size}");

            var keys = ScanKeys(multiplexer, endpoint.Database, 21);
            var hasMore = keys.Count > 20;

            if (keys.Count == 0)
            {
                ConsoleOut.Line(string.Empty);
                ConsoleOut.Line("当前数据库为空");
            }
            else
            {
                ConsoleOut.Line(string.Empty);
                ConsoleOut.Line("当前数据库中的键 (最多显示 20 个):");

                foreach (var key in keys.Take(20))
                {
                    ConsoleOut.Line($"  - {key} ({database.KeyType(key)})");
                }

                if (hasMore)
                {
                    ConsoleOut.Line("  ... 还有更多键");
                }
            }

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("连接信息:");
            ConsoleOut.Line($"  主机: {endpoint.Host}");
            ConsoleOut.Line($"  端口: {endpoint.Port}");
            ConsoleOut.Line($"  数据库: db{endpoint.Database}");
            ConsoleOut.Line($"  密码: {(endpoint.Password.Length > 0 ? "已设置" : "无")}");

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("连接已关闭。");
            return 0;
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ 连接失败: {error.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 用 SCAN 代替 KEYS：真实库可能有几十万键，KEYS 会阻塞服务器。
    /// 只需要前 21 个键就能展示，并据此判断“还有更多”。
    /// </summary>
    private static List<string> ScanKeys(IConnectionMultiplexer multiplexer, int database, int limit)
    {
        var keys = new List<string>();
        var server = multiplexer.GetServers().FirstOrDefault();

        if (server is null)
        {
            return keys;
        }

        var cursor = 0L;

        do
        {
            var batch = server.Execute("SCAN", cursor.ToString(CultureInfo.InvariantCulture),
                "COUNT", "200", "MATCH", "*");

            var parts = (RedisResult[])batch!;
            cursor = long.Parse((string)parts[0]!, CultureInfo.InvariantCulture);

            foreach (var key in (RedisResult[])parts[1]!)
            {
                keys.Add((string)key!);

                if (keys.Count >= limit)
                {
                    return keys;
                }
            }
        }
        while (cursor != 0);

        return keys;
    }

    private static string Match(string info, string field)
    {
        foreach (var line in info.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith(field + ":", StringComparison.Ordinal))
            {
                return trimmed[(field.Length + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static Endpoint ParseEndpoint(CliArgs parsed)
    {
        var port = 6379;
        var portText = parsed.Get("-p");

        if (portText is not null &&
            int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
        {
            port = parsedPort;
        }

        var database = 0;
        var dbText = parsed.Get("-d");

        if (dbText is not null &&
            int.TryParse(dbText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDb))
        {
            database = parsedDb;
        }

        return new Endpoint(
            parsed.Positionals[0],
            port,
            database,
            parsed.Get("--password") ?? string.Empty,
            parsed.Get("--user") ?? string.Empty,
            parsed.Has("--secure"));
    }

    /// <summary>
    /// 连接参数：AllowAdmin 是必须的，否则 INFO / CONFIG 这类管理命令会被驱动直接拒绝。
    /// </summary>
    private static ConnectionMultiplexer Connect(Endpoint endpoint)
    {
        var options = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            AllowAdmin = true,
            ConnectTimeout = 10000,
            SyncTimeout = 10000,
            DefaultDatabase = endpoint.Database,
            Ssl = endpoint.Secure,
        };

        options.EndPoints.Add(endpoint.Host, endpoint.Port);

        if (endpoint.Password.Length > 0)
        {
            options.Password = endpoint.Password;
        }

        if (endpoint.User.Length > 0)
        {
            options.User = endpoint.User;
        }

        return ConnectionMultiplexer.Connect(options);
    }

    internal sealed record Endpoint(string Host, int Port, int Database, string Password, string User, bool Secure);
}
