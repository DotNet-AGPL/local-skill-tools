using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Core.Cli;
using Connector.Core.Output;
using Microsoft.Data.SqlClient;

return CliHost.Run(() => SqlServerCli.Run(args));

internal static class SqlServerCli
{
    private const string QueryUsage =
        "用法: db-sqlserver query <host> <user> <password> <database> [-p port] -q \"SQL\" [-o json|csv|table] [--integrated]\n" +
        "      db-sqlserver query <host> <user> <password> <database> [-p port] -f script.sql|-";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-q", "--query"),
        OptionDef.Value("-f", "--file"),
        OptionDef.Value("-o", "--output"),
        OptionDef.Flag("--integrated"),
    ];

    private static readonly string[] OutputFormats = ["json", "csv", "table"];

    private static readonly Regex QueryPattern = new(
        @"^\s*(SELECT|WITH|EXEC|EXECUTE|DECLARE|PRINT)\b",
        RegexOptions.IgnoreCase);

    public const int DefaultPort = 1433;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-sqlserver connect <host> <user> <password> <database> [-p port] [--integrated]");
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

        if (sql.Trim().Length == 0)
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

    private static int Execute(Endpoint endpoint, string sql, string format)
    {
        using var connection = Open(endpoint);

        ConsoleOut.Info("正在执行查询...");

        var stopwatch = Stopwatch.StartNew();
        var isQuery = QueryPattern.IsMatch(sql);

        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var affected = 0;

        if (isQuery)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();

            var names = new string[reader.FieldCount];
            var typeNames = new string[reader.FieldCount];

            for (var i = 0; i < reader.FieldCount; i++)
            {
                names[i] = reader.GetName(i);
                typeNames[i] = reader.GetDataTypeName(i);
            }

            foreach (var name in names)
            {
                if (!columns.Contains(name))
                {
                    columns.Add(name);
                }
            }

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);

                for (var i = 0; i < names.Length; i++)
                {
                    row[names[i]] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i), typeNames[i]);
                }

                rows.Add(row);
            }
        }
        else
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            affected = command.ExecuteNonQuery();

            // DDL 在 SQL Server 里返回 -1，统一按 0 展示
            if (affected < 0)
            {
                affected = 0;
            }
        }

        stopwatch.Stop();

        ConsoleOut.Info(string.Empty);
        ConsoleOut.Info($"查询执行完成 ({stopwatch.ElapsedMilliseconds}ms)");

        if (isQuery)
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
                WriteRows(columns, rows, format);
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

    /// <summary>
    /// SQL Server 把 date 也映射成 DateTime（零点），按声明类型还原成 DateOnly，
    /// 与其它连接器的日期输出保持一致。
    /// </summary>
    private static object? Normalize(object? value, string declaredType)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is DateTime dateTime &&
            (string.Equals(declaredType, "date", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(declaredType, "DATE", StringComparison.OrdinalIgnoreCase)))
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return value;
    }

    private static void WriteRows(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
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
                ConsoleOut.Block(JsonText.Rows(columns, rows));
                break;
        }
    }

    private static int Connect(string[] args)
    {
        if (args.Length < 4)
        {
            ConsoleOut.Error("用法: db-sqlserver connect <host> <user> <password> <database> [-p port] [--integrated]");
            ConsoleOut.Error("示例: db-sqlserver connect localhost sa password123 mydb -p 1433");
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
            ConsoleOut.Error("用法: db-sqlserver connect <host> <user> <password> <database> [-p port] [--integrated]");
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);

        try
        {
            ConsoleOut.Line($"正在连接到 SQL Server: {endpoint.Host}{endpoint.PortSuffix}...");

            using var connection = Open(endpoint);
            ConsoleOut.Line("✓ 连接成功！");
            ConsoleOut.Line($"服务端版本: {connection.ServerVersion}");

            var tables = new List<string>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT TABLE_SCHEMA + '.' + TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                    "WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_SCHEMA, TABLE_NAME";

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
            ConsoleOut.Line($"  端口: {(endpoint.Integrated ? "默认" : endpoint.Port.ToString(CultureInfo.InvariantCulture))}");
            ConsoleOut.Line($"  用户: {(endpoint.Integrated ? "Windows 集成认证" : endpoint.User)}");
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
            port,
            parsed.Has("--integrated"));
    }

    private static SqlConnection Open(Endpoint endpoint)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = endpoint.PortSuffix.Length > 0 ? $"{endpoint.Host},{endpoint.Port}" : endpoint.Host,
            InitialCatalog = endpoint.Database,
            TrustServerCertificate = true,
            Encrypt = true,
            Pooling = false,
            ConnectTimeout = 15,
        };

        if (endpoint.Integrated)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = endpoint.User;
            builder.Password = endpoint.Password;
        }

        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    internal sealed record Endpoint(string Host, string User, string Password, string Database, int Port, bool Integrated)
    {
        /// <summary>LocalDB 这类实例名不带端口，需要和「主机,端口」形式区分。</summary>
        public string PortSuffix => Host.StartsWith('(') || Host.Contains('\\') ? string.Empty : $",{Port}";
    }
}
