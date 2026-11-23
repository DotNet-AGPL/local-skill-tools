using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClickHouse.Driver;
using Connector.Core.Cli;
using Connector.Core.Output;

return CliHost.Run(() => ClickHouseCli.Run(args));

internal static class ClickHouseCli
{
    private const string QueryUsage =
        "用法: db-clickhouse query <host> [-p port] [-u user] [-d database] [--password pw] [--secure] -q \"SQL\" [-o json|csv|table]\n" +
        "      db-clickhouse query <host> [...] -f script.sql|-";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-u", "--user"),
        OptionDef.Value("-d", "--database"),
        OptionDef.Value("--password"),
        OptionDef.Value("-q", "--query"),
        OptionDef.Value("-f", "--file"),
        OptionDef.Value("-o", "--output"),
        OptionDef.Flag("--secure"),
    ];

    private static readonly string[] OutputFormats = ["json", "csv", "table"];

    private static readonly Regex QueryPattern = new(
        @"^\s*(SELECT|WITH|SHOW|DESCRIBE|DESC|EXPLAIN|VALUES|EXISTS)\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex FormatPattern = new(
        @"\bFORMAT\s+[A-Za-z0-9_]+\s*;?\s*$",
        RegexOptions.IgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-clickhouse connect <host> [-p port] [-u user] [-d database] [--password pw] [--secure]");
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
        using var client = new ClickHouseClient(BuildConnectionString(endpoint));

        ConsoleOut.Info("正在执行查询...");

        var stopwatch = Stopwatch.StartNew();
        var isQuery = QueryPattern.IsMatch(sql);

        if (!isQuery)
        {
            var affected = client.ExecuteNonQueryAsync(sql).GetAwaiter().GetResult();
            stopwatch.Stop();

            ConsoleOut.Info(string.Empty);
            ConsoleOut.Info($"查询执行完成 ({stopwatch.ElapsedMilliseconds}ms)");
            ConsoleOut.Info($"影响 {affected} 行");
            ConsoleOut.Info(string.Empty);
            ConsoleOut.Info("✓ 查询执行成功");
            return 0;
        }

        var payload = FormatPattern.IsMatch(sql) ? sql : sql.TrimEnd().TrimEnd(';') + "\nFORMAT JSON";

        using var raw = client.ExecuteRawResultAsync(payload).GetAwaiter().GetResult();
        var text = raw.ReadAsStringAsync().GetAwaiter().GetResult();

        stopwatch.Stop();

        var (columns, rows, kinds) = ParseJsonResult(text);

        ConsoleOut.Info(string.Empty);
        ConsoleOut.Info($"查询执行完成 ({stopwatch.ElapsedMilliseconds}ms)");

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

        ConsoleOut.Info(string.Empty);
        ConsoleOut.Info("✓ 查询执行成功");
        return 0;
    }

    /// <summary>
    /// 解析 FORMAT JSON 的响应：meta 提供列名与类型，data 提供行数据。
    /// 不使用 ADO.NET 的 RowBinary reader：它在 AOT 下依赖表达式树，Tuple/Map 列会直接崩。
    /// </summary>
    private static (List<string> Columns, List<IReadOnlyDictionary<string, object?>> Rows, Dictionary<string, ColumnKind> Kinds)
        ParseJsonResult(string text)
    {
        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var kinds = new Dictionary<string, ColumnKind>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(text))
        {
            return (columns, rows, kinds);
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        var types = new List<string>();

        if (root.TryGetProperty("meta", out var meta))
        {
            foreach (var column in meta.EnumerateArray())
            {
                var name = column.GetProperty("name").GetString() ?? string.Empty;
                var type = column.GetProperty("type").GetString() ?? string.Empty;
                columns.Add(name);
                types.Add(type);

                if (string.Equals(type, "JSON", StringComparison.OrdinalIgnoreCase))
                {
                    kinds[name] = ColumnKind.RawJson;
                }
            }
        }

        if (!root.TryGetProperty("data", out var data))
        {
            return (columns, rows, kinds);
        }

        foreach (var rowElement in data.EnumerateArray())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);

            for (var i = 0; i < columns.Count; i++)
            {
                row[columns[i]] = rowElement.TryGetProperty(columns[i], out var value)
                    ? ConvertValue(value, types[i])
                    : null;
            }

            rows.Add(row);
        }

        return (columns, rows, kinds);
    }

    private static object? ConvertValue(JsonElement element, string clickHouseType)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var type = Unwrap(clickHouseType);

        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object?>();

            foreach (var item in element.EnumerateArray())
            {
                items.Add(ConvertValue(item, "String"));
            }

            return items;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                map[property.Name] = ConvertValue(property.Value, "String");
            }

            return map;
        }

        if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        if (type.StartsWith("Decimal", StringComparison.Ordinal))
        {
            return element.TryGetDecimal(out var dec) ? dec : element.GetRawText();
        }

        if (type.StartsWith("Float", StringComparison.Ordinal))
        {
            return element.GetDouble();
        }

        if (type.StartsWith("Int", StringComparison.Ordinal))
        {
            return element.TryGetInt64(out var int64) ? int64 : (object)element.GetRawText();
        }

        if (type.StartsWith("UInt", StringComparison.Ordinal))
        {
            return element.TryGetUInt64(out var uint64) ? uint64 : (object)element.GetRawText();
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out var number) ? number : (object)element.GetRawText();
        }

        var text = element.GetString() ?? string.Empty;

        // ClickHouse 的 DateTime 用空格分隔，统一成与其它连接器一致的 ISO 形式
        if (type.StartsWith("DateTime", StringComparison.Ordinal) && text.Length >= 19)
        {
            return text.Replace(' ', 'T');
        }

        return text;
    }

    private static string Unwrap(string type)
    {
        if (type.StartsWith("Nullable(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            return type[9..^1];
        }

        if (type.StartsWith("LowCardinality(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            return type[15..^1];
        }

        return type;
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

    private static int Connect(string[] args)
    {
        if (args.Length < 1 || args[0].StartsWith('-'))
        {
            ConsoleOut.Error("用法: db-clickhouse connect <host> [-p port] [-u user] [-d database] [--password pw] [--secure]");
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
            ConsoleOut.Error("用法: db-clickhouse connect <host> [-p port] [-u user] [-d database] [--password pw] [--secure]");
            return 1;
        }

        var endpoint = ParseEndpoint(parsed);

        try
        {
            ConsoleOut.Line($"正在连接到 ClickHouse 服务器: {endpoint.Host}:{endpoint.Port}...");

            using var client = new ClickHouseClient(BuildConnectionString(endpoint));
            ConsoleOut.Line("✓ 连接成功！");

            var version = QueryScalar(client, "SELECT version()");
            ConsoleOut.Line($"ClickHouse 版本: {version}");

            var tables = new List<string>();

            using (var raw = client.ExecuteRawResultAsync(
                       $"SELECT name FROM system.tables WHERE database = currentDatabase() ORDER BY name FORMAT JSON")
                       .GetAwaiter().GetResult())
            {
                var text = raw.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    using var document = JsonDocument.Parse(text);

                    foreach (var row in document.RootElement.GetProperty("data").EnumerateArray())
                    {
                        tables.Add(row.GetProperty("name").GetString() ?? string.Empty);
                    }
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

    private static string QueryScalar(ClickHouseClient client, string sql)
    {
        using var raw = client.ExecuteRawResultAsync(sql + " FORMAT JSON").GetAwaiter().GetResult();
        var text = raw.ReadAsStringAsync().GetAwaiter().GetResult();

        using var document = JsonDocument.Parse(text);
        var data = document.RootElement.GetProperty("data");

        if (data.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = data[0];
        var property = first.EnumerateObject().First();
        return property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() ?? string.Empty
            : property.Value.GetRawText();
    }

    private static Endpoint ParseEndpoint(CliArgs parsed)
    {
        var secure = parsed.Has("--secure");
        var port = secure ? 8443 : 8123;
        var portText = parsed.Get("-p");

        if (portText is not null &&
            int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
        {
            port = parsedPort;
        }

        return new Endpoint(
            parsed.Positionals[0],
            parsed.Get("-u") ?? "default",
            parsed.Get("--password") ?? string.Empty,
            parsed.Get("-d") ?? "default",
            port,
            secure);
    }

    private static string BuildConnectionString(Endpoint endpoint)
    {
        var protocol = endpoint.Secure ? "https" : "http";

        return $"Host={endpoint.Host};Port={endpoint.Port};Username={endpoint.User};" +
               $"Password={endpoint.Password};Database={endpoint.Database};Protocol={protocol}";
    }

    internal sealed record Endpoint(string Host, string User, string Password, string Database, int Port, bool Secure);
}
