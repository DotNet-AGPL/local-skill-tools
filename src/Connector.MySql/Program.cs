using System.Diagnostics;
using System.Globalization;
using Connector.Core.Cli;
using Connector.Core.Output;
using MySqlConnector;

return CliHost.Run(() => MySqlCli.Run(args));

internal static class MySqlCli
{
    private const string QueryUsage =
        "用法: db-mysql query <host> <user> <password> <database> [-p port] -q \"SQL\" [-o json|csv|table]\n" +
        "      db-mysql query <host> <user> <password> <database> [-p port] -f script.sql|-";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-q", "--query"),
        OptionDef.Value("-f", "--file"),
        OptionDef.Value("-o", "--output"),
    ];

    private static readonly string[] OutputFormats = ["json", "csv", "table"];

    public const int DefaultPort = 3306;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-mysql connect <host> <user> <password> <database> [-p port]");
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
            return Execute(endpoint, sql, format);
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ 查询执行失败: {error.Message}");
            return 1;
        }
    }

    private static int Connect(string[] args)
    {
        if (args.Length < 4)
        {
            ConsoleOut.Error("用法: db-mysql connect <host> <user> <password> <database> [-p port]");
            ConsoleOut.Error("示例: db-mysql connect localhost root password123 mydb -p 3306");
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
            ConsoleOut.Error("用法: db-mysql connect <host> <user> <password> <database> [-p port]");
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);

        try
        {
            ConsoleOut.Line($"正在连接到 MySQL 服务器: {endpoint.Host}:{endpoint.Port}...");

            using var connection = Open(endpoint);
            ConsoleOut.Line("✓ 连接成功！");

            var version = ScalarString(connection, "SELECT VERSION() AS version");
            ConsoleOut.Line($"MySQL 版本: {version}");

            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SHOW TABLES";

                using var reader = command.ExecuteReader();

                while (reader.Read())
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

    private static int Execute(Endpoint endpoint, string sql, string format)
    {
        using var connection = Open(endpoint);
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        ConsoleOut.Info("正在执行查询...");

        var stopwatch = Stopwatch.StartNew();

        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var kinds = new Dictionary<string, ColumnKind>(StringComparer.Ordinal);
        long affected = 0;
        long insertId = 0;

        using (var reader = command.ExecuteReader())
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

                    if (string.Equals(reader.GetDataTypeName(i), "JSON", StringComparison.OrdinalIgnoreCase))
                    {
                        kinds[names[i]] = ColumnKind.RawJson;
                    }
                }

                while (reader.Read())
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);

                    for (var i = 0; i < names.Length; i++)
                    {
                        row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
                }

                // 多语句时把后面的语句也执行掉，只输出第一个结果集
                while (reader.NextResult())
                {
                    // 仅推进，不采集
                }
            }
        }

        if (columns.Count == 0)
        {
            using var summary = connection.CreateCommand();
            summary.CommandText = "SELECT ROW_COUNT() AS affected, LAST_INSERT_ID() AS lastId";

            using var summaryReader = summary.ExecuteReader();

            if (summaryReader.Read())
            {
                affected = summaryReader.IsDBNull(0) ? 0 : summaryReader.GetInt64(0);
                insertId = summaryReader.IsDBNull(1) ? 0 : summaryReader.GetInt64(1);
            }

            if (affected < 0)
            {
                affected = 0;
            }
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

            if (insertId > 0)
            {
                ConsoleOut.Info($"新插入记录 ID: {insertId}");
            }
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

        if (portText is not null && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
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
    /// 连接参数与 Node 版对齐：不做 TLS 强制（Preferred，失败回退），字符集 utf8mb4，
    /// 关闭连接池（Node 版每次都是独立连接），TINYINT(1) 按驱动默认映射为 bool。
    /// </summary>
    private static MySqlConnection Open(Endpoint endpoint)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = endpoint.Host,
            Port = (uint)endpoint.Port,
            UserID = endpoint.User,
            Password = endpoint.Password,
            Database = endpoint.Database,
            CharacterSet = "utf8mb4",
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 15,
            Pooling = false,
        };

        var connection = new MySqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string ScalarString(MySqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();

        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal sealed record Endpoint(string Host, string User, string Password, string Database, int Port);
}
