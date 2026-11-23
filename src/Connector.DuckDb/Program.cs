using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Core.Cli;
using Connector.Core.Output;
using DuckDB.NET.Data;

return CliHost.Run(() => DuckDbCli.Run(args));

internal static class DuckDbCli
{
    private const string MemoryDatabase = ":memory:";

    private const string QueryUsage =
        "用法: db-duckdb query <file.duckdb> -q \"SQL\" [-o json|csv|table] [--readonly]\n" +
        "      db-duckdb query <file.duckdb> -f script.sql|-\n" +
        "      db-duckdb query :memory: -q \"SQL\"";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-q", "--query"),
        OptionDef.Value("-f", "--file"),
        OptionDef.Value("-o", "--output"),
        OptionDef.Flag("--readonly"),
    ];

    private static readonly string[] OutputFormats = ["json", "csv", "table"];

    // DuckDB 支持多语句，但这里与 SQLite 一致：仅首关键字决定走查询还是执行路径
    private static readonly Regex QueryPattern = new(
        @"^\s*(SELECT|WITH|PRAGMA|EXPLAIN|VALUES|FROM|SHOW|DESCRIBE|SUMMARIZE|PIVOT|UNPIVOT)\b",
        RegexOptions.IgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-duckdb connect <file.duckdb>");
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

        var databasePath = parsed.Positionals[0];
        var sql = parsed.Get("-q") ?? string.Empty;
        var scriptFile = parsed.Get("-f");
        var format = (parsed.Get("-o") ?? "json").ToLowerInvariant();
        var readOnly = parsed.Has("--readonly");

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

        var isMemory = string.Equals(databasePath, MemoryDatabase, StringComparison.Ordinal);

        if (!isMemory && !File.Exists(databasePath))
        {
            ConsoleOut.Error($"错误: 数据库文件不存在: {databasePath}");
            return 1;
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
            return Execute(databasePath, sql, format, readOnly && !isMemory);
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ 查询执行失败: {error.Message}");
            return 1;
        }
    }

    private static int Execute(string databasePath, string sql, string format, bool readOnly)
    {
        using var connection = Open(databasePath, readOnly);

        ConsoleOut.Info("正在执行查询...");

        var stopwatch = Stopwatch.StartNew();
        var isQuery = QueryPattern.IsMatch(sql);

        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var kinds = new Dictionary<string, ColumnKind>(StringComparer.Ordinal);
        var affected = 0;

        if (isQuery)
        {
            ReadResultSet(connection, sql, columns, rows);
        }
        else
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                affected = command.ExecuteNonQuery();
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

    private static void ReadResultSet(
        DuckDBConnection connection,
        string sql,
        List<string> columns,
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        var names = new string[reader.FieldCount];

        for (var i = 0; i < reader.FieldCount; i++)
        {
            names[i] = reader.GetName(i);
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
                row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        // 多语句时把后面的语句也执行掉，只输出第一个结果集
        while (reader.NextResult())
        {
        }
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
            ConsoleOut.Error("用法: db-duckdb connect <file.duckdb>");
            ConsoleOut.Error("示例: db-duckdb connect ./data/analytics.duckdb");
            return 1;
        }

        var databasePath = args[0];
        var isMemory = string.Equals(databasePath, MemoryDatabase, StringComparison.Ordinal);
        var fileExists = !isMemory && File.Exists(databasePath);

        if (!isMemory && !fileExists)
        {
            ConsoleOut.Line($"数据库文件不存在，将创建新数据库: {databasePath}");
        }

        try
        {
            ConsoleOut.Line($"正在打开 DuckDB 数据库: {databasePath}...");

            using var connection = Open(databasePath, readOnly: false);

            ConsoleOut.Line("✓ 连接成功！");

            var version = ScalarString(connection, "SELECT version()");
            if (version.Length > 0)
            {
                ConsoleOut.Line($"DuckDB 版本: {version}");
            }

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("数据库中的表:");

            var tables = new List<string>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT table_name FROM duckdb_tables() WHERE NOT internal ORDER BY table_name";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

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

            if (fileExists)
            {
                var sizeKb = new FileInfo(databasePath).Length / 1024d;
                ConsoleOut.Line(string.Empty);
                ConsoleOut.Line($"数据库文件大小: {sizeKb.ToString("0.00", CultureInfo.InvariantCulture)} KB");
            }

            ConsoleOut.Line($"数据库路径: {(isMemory ? MemoryDatabase : Path.GetFullPath(databasePath))}");

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

    private static DuckDBConnection Open(string databasePath, bool readOnly)
    {
        var connectionString = $"Data Source={databasePath}";

        if (readOnly)
        {
            connectionString += ";ACCESS_MODE=READ_ONLY";
        }

        var connection = new DuckDBConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static string ScalarString(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();

        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
