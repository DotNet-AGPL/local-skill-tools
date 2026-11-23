using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Core.Cli;
using Connector.Core.Output;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

return CliHost.Run(() => OracleCli.Run(args));

internal static class OracleCli
{
    private const string QueryUsage =
        "用法: db-oracle query <host> <user> <password> <service> [-p port] -q \"SQL\" [-o json|csv|table]\n" +
        "      db-oracle query <host> <user> <password> <service> [-p port] -f script.sql|-";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-q", "--query"),
        OptionDef.Value("-f", "--file"),
        OptionDef.Value("-o", "--output"),
    ];

    private static readonly string[] OutputFormats = ["json", "csv", "table"];

    private static readonly Regex QueryPattern = new(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase);

    public const int DefaultPort = 1521;

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-oracle connect <host> <user> <password> <service> [-p port]");
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
    /// Oracle 的 DATE 也带时间（默认零点），按声明类型还原成 DateOnly；
    /// CLOB/BLOB 统一成字符串与 byte[]。
    /// </summary>
    private static object? Normalize(object? value, string declaredType)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is DateTime dateTime && string.Equals(declaredType, "DATE", StringComparison.OrdinalIgnoreCase))
        {
            return DateOnly.FromDateTime(dateTime);
        }

        if (value is OracleClob clob)
        {
            return clob.Value;
        }

        if (value is OracleBlob blob)
        {
            return blob.Value;
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
            ConsoleOut.Error("用法: db-oracle connect <host> <user> <password> <service> [-p port]");
            ConsoleOut.Error("示例: db-oracle connect localhost system password FREEPDB1 -p 1521");
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
            ConsoleOut.Error("用法: db-oracle connect <host> <user> <password> <service> [-p port]");
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);

        try
        {
            ConsoleOut.Line($"正在连接到 Oracle: {endpoint.Host}:{endpoint.Port}/{endpoint.Service}...");

            using var connection = Open(endpoint);
            ConsoleOut.Line("✓ 连接成功！");
            ConsoleOut.Line($"服务端版本: {connection.ServerVersion}");

            var tables = new List<string>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT TABLE_NAME FROM USER_TABLES ORDER BY TABLE_NAME";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("当前用户的表:");

            if (tables.Count == 0)
            {
                ConsoleOut.Line("  (空)");
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
            ConsoleOut.Line($"  服务名: {endpoint.Service}");

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
            port);
    }

    /// <summary>
    /// 用显式 TNS 描述符而不是 Easy Connect：ODP.NET 在 AOT 下对 //host:port/service 的解析会失败（ORA-50201）。
    /// </summary>
    private static OracleConnection Open(Endpoint endpoint)
    {
        var descriptor =
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={endpoint.Host})(PORT={endpoint.Port}))" +
            $"(CONNECT_DATA=(SERVICE_NAME={endpoint.Service})))";

        var connectionString =
            $"User Id={endpoint.User};Password={endpoint.Password};Data Source={descriptor};Connection Timeout=15";

        var connection = new OracleConnection(connectionString);
        connection.Open();
        return connection;
    }

    internal sealed record Endpoint(string Host, string User, string Password, string Service, int Port);
}
