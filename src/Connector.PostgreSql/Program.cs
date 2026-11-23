using System.Diagnostics;
using System.Globalization;
using Connector.Core.Cli;
using Connector.Core.Output;
using Npgsql;

return await CliHost.RunAsync(() => PostgresCli.RunAsync(args));

internal static class PostgresCli
{
    private const string QueryUsage =
        "用法: db-postgres query <host> <user> <password> <database> [-p port] -q \"SQL\" [-o json|csv|table]\n" +
        "      db-postgres query <host> <user> <password> <database> [-p port] -f script.sql|-";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-q", "--query"),
        OptionDef.Value("-f", "--file"),
        OptionDef.Value("-o", "--output"),
    ];

    private static readonly string[] OutputFormats = ["json", "csv", "table"];

    public const int DefaultPort = 5432;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-postgres connect <host> <user> <password> <database> [-p port]");
            return 1;
        }

        var command = args[0];
        var rest = args[1..];

        return command switch
        {
            "query" => await QueryAsync(rest),
            "connect" => await ConnectAsync(rest),
            _ => UnknownCommand(command),
        };
    }

    private static int UnknownCommand(string command)
    {
        ConsoleOut.Error($"未知子命令: {command}");
        ConsoleOut.Error(QueryUsage);
        return 1;
    }

    private static async Task<int> QueryAsync(string[] args)
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

        if (parsed.Positionals.Count < 4)
        {
            ConsoleOut.Error(QueryUsage);
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);

        var sql = parsed.Get("-q") ?? string.Empty;
        var scriptFile = parsed.Get("-f");
        var format = (parsed.Get("-o") ?? "json").ToLowerInvariant();

        if (scriptFile is not null)
        {
            if (scriptFile == "-")
            {
                // 从 stdin 读整段 SQL：完全绕过 shell 解析，反引号 / $ / ! 都不会被吞
                sql = Console.In.ReadToEnd();
            }
            else if (!File.Exists(scriptFile))
            {
                ConsoleOut.Error($"错误: SQL 文件不存在: {scriptFile}");
                return 1;
            }
            else
            {
                sql = File.ReadAllText(scriptFile);
            }
        }

        if (sql.Length == 0)
        {
            ConsoleOut.Error("错误: 请提供 SQL 查询语句 (-q) 或 SQL 文件 (-f)");
            return 1;
        }

        if (!OutputFormats.Contains(format))
        {
            ConsoleOut.Error("错误: 输出格式必须是 json、csv 或 table");
            return 1;
        }

        try
        {
            return await ExecuteAsync(endpoint, sql, format);
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ 查询执行失败: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> ConnectAsync(string[] args)
    {
        if (args.Length < 4)
        {
            ConsoleOut.Error("用法: db-postgres connect <host> <user> <password> <database> [-p port]");
            ConsoleOut.Error("示例: db-postgres connect localhost postgres password123 mydb -p 5432");
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

        if (parsed.Positionals.Count < 4)
        {
            ConsoleOut.Error("用法: db-postgres connect <host> <user> <password> <database> [-p port]");
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);

        try
        {
            ConsoleOut.Line($"正在连接到 PostgreSQL 服务器: {endpoint.Host}:{endpoint.Port}...");

            await using var connection = await OpenAsync(endpoint);
            ConsoleOut.Line("✓ 连接成功！");

            var version = await ScalarStringAsync(connection, "SELECT version()");
            ConsoleOut.Line($"PostgreSQL 版本: {version}");

            var tables = new List<string>();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename";

                await using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line($"数据库 \"{endpoint.Database}\" 中的表:");

            if (tables.Count == 0)
            {
                ConsoleOut.Line("  (空数据库)");
            }
            else
            {
                foreach (var table in tables)
                {
                    ConsoleOut.Line($"  - {table}");
                }
            }

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("连接信息:");
            ConsoleOut.Line($"  主机: {endpoint.Host}");
            ConsoleOut.Line($"  端口: {endpoint.Port}");
            ConsoleOut.Line($"  用户: {endpoint.User}");
            ConsoleOut.Line($"  数据库: {endpoint.Database}");

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

    private static async Task<int> ExecuteAsync(Endpoint endpoint, string sql, string format)
    {
        await using var connection = await OpenAsync(endpoint);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        ConsoleOut.Info("正在执行查询...");

        var stopwatch = Stopwatch.StartNew();

        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var kinds = new Dictionary<string, ColumnKind>(StringComparer.Ordinal);
        var affected = 0;

        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (reader.FieldCount > 0)
            {
                var names = new string[reader.FieldCount];

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    names[i] = reader.GetName(i);
                }

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (!columns.Contains(names[i]))
                    {
                        columns.Add(names[i]);
                    }

                    var typeName = reader.GetDataTypeName(i);

                    if (string.Equals(typeName, "json", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(typeName, "jsonb", StringComparison.OrdinalIgnoreCase))
                    {
                        kinds[names[i]] = ColumnKind.RawJson;
                    }
                }

                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);

                    for (var i = 0; i < names.Length; i++)
                    {
                        row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
                }

                // 多语句时把后面的语句也执行掉，只输出第一个结果集
                while (await reader.NextResultAsync())
                {
                }
            }
            else
            {
                affected = reader.RecordsAffected;
            }
        }

        if (affected < 0)
        {
            affected = 0;
        }

        stopwatch.Stop();

        ConsoleOut.Info(string.Empty);
        ConsoleOut.Info($"查询执行完成 ({stopwatch.ElapsedMilliseconds}ms)");

        if (columns.Count > 0)
        {
            if (rows.Count == 0)
            {
                ConsoleOut.Info("返回 0 行数据");
            }
            else
            {
                ConsoleOut.Info($"返回 {rows.Count} 行数据");
                ConsoleOut.Info(string.Empty);
                ConsoleOut.Info("--- 查询结果 ---");
                ConsoleOut.Info(string.Empty);
                WriteRows(columns, rows, kinds, format);
            }
        }
        else
        {
            ConsoleOut.Info($"影响 {affected} 行");
        }

        ConsoleOut.Info(string.Empty);
        ConsoleOut.Info("✓ 查询执行成功");
        return 0;
    }

    private static void WriteRows(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyDictionary<string, ColumnKind> kinds,
        string format)
    {
        switch (format)
        {
            case "csv":
                TextOut.Csv(columns, rows);
                break;
            case "table":
                TextOut.Table(columns, rows);
                break;
            default:
                ConsoleOut.Block(JsonText.Rows(columns, rows, kinds));
                break;
        }
    }

    private static Endpoint ParseEndpoint(CliArgs parsed)
    {
        var port = DefaultPort;
        var portText = parsed.Get("-p");

        if (portText is not null &&
            int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
        {
            port = parsedPort;
        }

        return new Endpoint(
            parsed.Positionals[0],
            parsed.Positionals[1],
            parsed.Positionals[2],
            parsed.Positionals[3],
            port);
    }

    /// <summary>
    /// 连接参数与 Node 版对齐：TLS 优先（Prefer，失败回退）、关闭连接池。
    /// Npgsql 全部走异步 API（11.x 计划弃用同步 API）。
    /// </summary>
    private static async Task<NpgsqlConnection> OpenAsync(Endpoint endpoint)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Username = endpoint.User,
            Password = endpoint.Password,
            Database = endpoint.Database,
            SslMode = SslMode.Prefer,
            Timeout = 15,
            Pooling = false,
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal sealed record Endpoint(string Host, string User, string Password, string Database, int Port);
}
