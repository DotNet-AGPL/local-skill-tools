using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Core.Cli;
using Connector.Core.Output;
using Microsoft.Data.Sqlite;

return CliHost.Run(() => SqliteCli.Run(args));

internal static class SqliteCli
{
    private const string QueryUsage =
        "用法: db-sqlite query <database.db> -q \"SQL\" [-o json|csv|table] [--readonly] [--checkpoint] [--no-checkpoint]\n" +
        "      db-sqlite query <database.db> -f script.sql|-\n" +
        "      db-sqlite query <database.db> --checkpoint";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-q", "--query"),
        OptionDef.Value("-f", "--file"),
        OptionDef.Value("-o", "--output"),
        OptionDef.Flag("--readonly"),
        OptionDef.Flag("--checkpoint"),
        OptionDef.Flag("--no-checkpoint"),
    ];

    private static readonly string[] OutputFormats = ["json", "csv", "table"];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-sqlite connect <database.db>");
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
        var checkpointOnly = parsed.Has("--checkpoint");
        var autoCheckpoint = !parsed.Has("--no-checkpoint");

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

        if (!File.Exists(databasePath))
        {
            ConsoleOut.Error($"错误: 数据库文件不存在: {databasePath}");
            return 1;
        }

        if (checkpointOnly && readOnly)
        {
            ConsoleOut.Error("错误: --checkpoint 需要读写权限，不能与 --readonly 同时使用");
            return 1;
        }

        if (!checkpointOnly && sql.Length == 0)
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
            return ExecuteQuery(databasePath, sql, format, readOnly, checkpointOnly, autoCheckpoint);
        }
        catch (Exception error)
        {
            ConsoleOut.Error($"✗ 查询执行失败: {error.Message}");
            return 1;
        }
    }

    private static int ExecuteQuery(
        string databasePath,
        string sql,
        string format,
        bool readOnly,
        bool checkpointOnly,
        bool autoCheckpoint)
    {
        using var connection = Open(databasePath, readOnly);

        var walPath = databasePath + "-wal";
        var walExists = File.Exists(walPath);
        var journalMode = ScalarText(connection, "PRAGMA journal_mode");

        if (checkpointOnly)
        {
            var (busy, log, checkpointed) = Checkpoint(connection);
            ConsoleOut.Info(
                $"✓ wal_checkpoint(TRUNCATE) 完成：busy={busy} log={log} checkpointed={checkpointed}（journal_mode={journalMode}）");
            return 0;
        }

        ConsoleOut.Info("正在执行查询...");

        if (walExists)
        {
            ConsoleOut.Info($"提示: 检测到 WAL 文件（-wal）；本工具按文件路径打开、WAL 感知，读写安全（journal_mode={journalMode}）");
        }

        var stopwatch = Stopwatch.StartNew();
        var isQuery = Regex.IsMatch(sql, @"^\s*(SELECT|WITH|PRAGMA|EXPLAIN|VALUES)\b", RegexOptions.IgnoreCase);

        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long? changes = null;
        long? lastId = null;

        if (isQuery)
        {
            ReadResultSet(connection, sql, columns, rows);
        }
        else
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            using (var summary = connection.CreateCommand())
            {
                summary.CommandText = "SELECT changes() AS changes, last_insert_rowid() AS lastId";

                using var reader = summary.ExecuteReader();
                if (reader.Read())
                {
                    changes = reader.GetInt64(0);
                    lastId = reader.GetInt64(1);
                }
            }

            if (autoCheckpoint)
            {
                try
                {
                    var (busy, log, checkpointed) = Checkpoint(connection);
                    ConsoleOut.Info($"已自动 wal_checkpoint(TRUNCATE)：busy={busy} log={log} checkpointed={checkpointed}");
                }
                catch (Exception error)
                {
                    ConsoleOut.Info($"checkpoint 跳过: {error.Message}");
                }
            }
        }

        stopwatch.Stop();
        ConsoleOut.Info(string.Empty);
        ConsoleOut.Info($"查询执行完成 ({stopwatch.ElapsedMilliseconds}ms)");

        if (!isQuery)
        {
            ConsoleOut.Info($"影响 {changes ?? 0} 行");

            if (lastId > 0)
            {
                ConsoleOut.Info($"新插入记录 ID: {lastId}");
            }
        }
        else if (rows.Count == 0)
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

        ConsoleOut.Info(string.Empty);
        ConsoleOut.Info("✓ 查询执行成功");
        return 0;
    }

    private static void ReadResultSet(
        SqliteConnection connection,
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

        // 脚本以 SELECT/PRAGMA 开头但后面还有语句时，继续推进以把它们真正执行掉，
        // 避免“只跑了第一条”这种静默漏执行。
        var hasMoreResults = false;

        while (reader.NextResult())
        {
            hasMoreResults = true;
        }

        if (hasMoreResults)
        {
            ConsoleOut.Error("提示: 脚本包含多条语句，仅输出第一个结果集");
        }
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
        if (args.Length < 1 || args[0].StartsWith('-'))
        {
            ConsoleOut.Error("用法: db-sqlite connect <database.db>");
            ConsoleOut.Error("示例: db-sqlite connect mydb.sqlite");
            return 1;
        }

        var databasePath = args[0];
        var fileExists = File.Exists(databasePath);

        if (!fileExists)
        {
            ConsoleOut.Line($"数据库文件不存在，将创建新数据库: {databasePath}");
        }

        try
        {
            ConsoleOut.Line($"正在打开 SQLite 数据库: {databasePath}...");

            using var connection = Open(databasePath, readOnly: false);

            ConsoleOut.Line("✓ 连接成功！");

            var version = ScalarText(connection, "SELECT sqlite_version() AS version");
            if (version.Length > 0)
            {
                ConsoleOut.Line($"SQLite 版本: {version}");
            }

            var journalMode = ScalarText(connection, "PRAGMA journal_mode");
            if (journalMode.Length > 0)
            {
                var walSuffix = File.Exists(databasePath + "-wal") ? "（检测到 -wal：本工具按路径打开，WAL 感知）" : string.Empty;
                ConsoleOut.Line($"journal_mode: {journalMode}{walSuffix}");
            }

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("数据库中的表:");

            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

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

            ConsoleOut.Line($"数据库路径: {Path.GetFullPath(databasePath)}");

            if (!fileExists)
            {
                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM";
                vacuum.ExecuteNonQuery();
                ConsoleOut.Line("✓ 新数据库文件已创建");
            }

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
    /// 打开连接。Foreign Keys 显式开启，与 node:sqlite 的 enableForeignKeyConstraints 默认值一致；
    /// Pooling 关闭，避免进程内残留句柄影响 checkpoint / 文件锁行为。
    /// </summary>
    private static SqliteConnection Open(string databasePath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();

        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static (string Busy, string Log, string Checkpointed) Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";

        using var reader = command.ExecuteReader();

        if (!reader.Read() || reader.FieldCount < 3)
        {
            return ("-", "-", "-");
        }

        return (
            reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
            reader.GetInt64(1).ToString(CultureInfo.InvariantCulture),
            reader.GetInt64(2).ToString(CultureInfo.InvariantCulture));
    }
}
