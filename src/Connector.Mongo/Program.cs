using System.Diagnostics;
using System.Globalization;
using System.Text;
using Connector.Core.Cli;
using Connector.Mongo;
using Connector.Core.Output;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

// NativeAOT 预热：驱动握手时会用反射构造这两个序列化器，这里先静态实例化并注册，
// 否则运行期会报 "no code was generated for it / missing native code"。
BsonSerializer.RegisterSerializer(
    typeof(List<object>),
    new EnumerableInterfaceImplementerSerializer<List<object>, object>(BsonSerializer.SerializerRegistry));
BsonSerializer.RegisterSerializer(
    typeof(System.Dynamic.ExpandoObject),
    new ExpandoObjectSerializer());

return CliHost.Run(() => MongoCli.Run(args));

internal static class MongoCli
{
    private const string QueryUsage =
        "用法: db-mongo query <host> <database> -c \"collection.method({...})\" [-p port] [-u user] [--password pw] [-o json|table]";

    private static readonly OptionDef[] QueryOptions =
    [
        OptionDef.Value("-p", "--port"),
        OptionDef.Value("-u", "--username"),
        OptionDef.Value("--password"),
        OptionDef.Value("-c", "--command"),
        OptionDef.Value("-o", "--output"),
    ];

    private static readonly string[] OutputFormats = ["json", "table"];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ConsoleOut.Error(QueryUsage);
            ConsoleOut.Error("      db-mongo connect <host> <database> [-p port] [-u user] [--password pw]");
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
        if (!TryParseArgs(args, out var parsed, out var error))
        {
            ConsoleOut.Error(error);
            ConsoleOut.Error(QueryUsage);
            return 1;
        }

        if (parsed.Positionals.Count < 2)
        {
            ConsoleOut.Error(QueryUsage);
            return 1;
        }

        var text = parsed.Get("-c") ?? string.Empty;

        if (text.Trim().Length == 0)
        {
            ConsoleOut.Error("错误: 请提供 MongoDB 操作命令 (-c)");
            return 1;
        }

        var format = (parsed.Get("-o") ?? "json").ToLowerInvariant();

        if (!OutputFormats.Contains(format))
        {
            ConsoleOut.Error("错误: 输出格式必须是 json 或 table");
            return 1;
        }

        var endpoint = Endpoint.From(parsed);

        try
        {
            var call = MongoCommand.Parse(text);

            using var client = Connect(endpoint);
            var database = client.GetDatabase(endpoint.Database);

            ConsoleOut.Info($"正在执行命令: {call.Target}.{call.Method}");

            var stopwatch = Stopwatch.StartNew();
            var result = Execute(database, call);
            stopwatch.Stop();

            ConsoleOut.Info(string.Empty);
            ConsoleOut.Info($"命令执行完成 ({stopwatch.ElapsedMilliseconds}ms)");

            if (result is object?[] array && array.Length == 0)
            {
                ConsoleOut.Info("返回 0 个文档");
            }
            else if (result is object?[] documents)
            {
                ConsoleOut.Info($"返回 {documents.Length} 个文档");
                ConsoleOut.Info(string.Empty);
                ConsoleOut.Info("--- 结果 ---");
                ConsoleOut.Info(string.Empty);
                WriteDocuments(documents, format);
            }
            else
            {
                ConsoleOut.Info(string.Empty);
                ConsoleOut.Info("--- 结果 ---");
                ConsoleOut.Info(string.Empty);
                ConsoleOut.Block(JsonText.Value(result));
            }

            ConsoleOut.Info(string.Empty);
            ConsoleOut.Info("✓ 命令执行成功");
            return 0;
        }
        catch (Exception exception)
        {
            ConsoleOut.Error($"✗ 命令执行失败: {exception.Message}");
            return 1;
        }
    }

    private static object? Execute(IMongoDatabase database, MongoCommand call)
    {
        var argument0 = call.Arguments.Count > 0 ? call.Arguments[0] : null;
        var argument1 = call.Arguments.Count > 1 ? call.Arguments[1] : null;
        var argument2 = call.Arguments.Count > 2 ? call.Arguments[2] : null;

        if (call.IsDatabaseCommand)
        {
            switch (call.Method)
            {
                case "stats":
                case "statsdb":
                    return Plain(database.RunCommand<BsonDocument>(new BsonDocument("dbStats", 1)));
                case "version":
                    return Plain(database.RunCommand<BsonDocument>(new BsonDocument("buildInfo", 1))["version"]);
                case "getCollectionNames":
                {
                    var names = database.ListCollectionNames().ToList();
                    return names.Cast<object?>().ToArray();
                }
                case "getCollectionInfos":
                {
                    var infos = database.ListCollections(new ListCollectionsOptions
                    {
                        Filter = argument0 is BsonDocument collectionFilter ? collectionFilter : FilterDefinition<BsonDocument>.Empty,
                    }).ToList();

                    return infos.Select(document => (object?)Plain(document)).ToArray();
                }
                case "dropDatabase":
                    database.Client.DropDatabase(database.DatabaseNamespace.DatabaseName);
                    return new Dictionary<string, object?> { ["dropped"] = true };
                case "runCommand":
                {
                    if (argument0 is not BsonDocument command)
                    {
                        throw new UsageException("runCommand 需要一个命令文档，例如 db.runCommand({\"ping\": 1})");
                    }

                    return Plain(database.RunCommand<BsonDocument>(command));
                }
                default:
                    throw new UsageException($"不支持的 db 命令: {call.Method}");
            }
        }

        var collection = database.GetCollection<BsonDocument>(call.Target);
        var filter = argument0 as BsonDocument ?? new BsonDocument();

        switch (call.Method)
        {
            case "find":
                return collection.Find(filter).ToList().Select(document => (object?)Plain(document)).ToArray();

            case "findOne":
            {
                var document = collection.Find(filter).FirstOrDefault();
                return document is null ? null : Plain(document);
            }

            case "countDocuments":
                return collection.CountDocuments(filter);

            case "estimatedDocumentCount":
                return collection.EstimatedDocumentCount();

            case "distinct":
            {
                var field = argument0?.AsString ?? throw new UsageException("distinct 需要一个字段名");
                var distinctFilter = argument1 as BsonDocument ?? new BsonDocument();
                return collection.Distinct<BsonValue>(field, distinctFilter)
                    .ToList()
                    .Select(value => (object?)Plain(value))
                    .ToArray();
            }

            case "aggregate":
            {
                if (argument0 is not BsonArray pipeline)
                {
                    throw new UsageException("aggregate 需要一个管道数组，例如 users.aggregate([{\"$match\": {}}])");
                }

                return collection.Aggregate<BsonDocument>(pipeline.Select(stage => stage.AsBsonDocument).ToList())
                    .ToList()
                    .Select(document => (object?)Plain(document))
                    .ToArray();
            }

            case "insertOne":
            {
                var document = RequireDocument(argument0, "insertOne");
                collection.InsertOne(document);
                return new Dictionary<string, object?>
                {
                    ["acknowledged"] = true,
                    ["insertedId"] = Plain(document.GetValue("_id", BsonNull.Value)),
                };
            }

            case "insertMany":
            {
                if (argument0 is not BsonArray documents)
                {
                    throw new UsageException("insertMany 需要一个文档数组");
                }

                collection.InsertMany(documents.Select(value => value.AsBsonDocument).ToList());
                return new Dictionary<string, object?> { ["insertedCount"] = documents.Count };
            }

            case "updateOne":
            case "updateMany":
            {
                var update = RequireDocument(argument1, call.Method);
                var result = call.Method == "updateOne"
                    ? collection.UpdateOne(filter, update)
                    : collection.UpdateMany(filter, update);

                return new Dictionary<string, object?>
                {
                    ["matched"] = result.MatchedCount,
                    ["modified"] = result.ModifiedCount,
                };
            }

            case "replaceOne":
            {
                var replacement = RequireDocument(argument1, "replaceOne");
                var result = collection.ReplaceOne(filter, replacement);

                return new Dictionary<string, object?>
                {
                    ["matched"] = result.MatchedCount,
                    ["modified"] = result.ModifiedCount,
                };
            }

            case "deleteOne":
            case "deleteMany":
            {
                var result = call.Method == "deleteOne"
                    ? collection.DeleteOne(filter)
                    : collection.DeleteMany(filter);

                return new Dictionary<string, object?> { ["deleted"] = result.DeletedCount };
            }

            case "createIndex":
            {
                var keys = RequireDocument(argument0, "createIndex");
                var options = argument1 as BsonDocument;
                var model = new CreateIndexModel<BsonDocument>(keys, options is null ? null : new CreateIndexOptions { Name = options.GetValue("name", BsonNull.Value).IsBsonNull ? null : options["name"].AsString });
                var name = collection.Indexes.CreateOne(model);
                return name;
            }

            case "drop":
                collection.Database.DropCollection(collection.CollectionNamespace.CollectionName);
                return new Dictionary<string, object?> { ["dropped"] = true };

            case "dropIndexes":
                collection.Indexes.DropAll();
                return new Dictionary<string, object?> { ["dropped"] = true };

            case "indexes":
                return collection.Indexes.List().ToList()
                    .Select(document => (object?)Plain(document))
                    .ToArray();

            default:
                throw new UsageException($"不支持的集合命令: {call.Method}");
        }
    }

    private static BsonDocument RequireDocument(BsonValue? value, string method)
    {
        if (value is BsonDocument document)
        {
            return document;
        }

        throw new UsageException($"{method} 需要一个文档参数，例如 {{\"id\": 1}}");
    }

    private static void WriteDocuments(object?[] documents, string format)
    {
        if (format == "table")
        {
            var rows = documents.OfType<Dictionary<string, object?>>().ToList();

            if (rows.Count == 0 || rows.Count != documents.Length)
            {
                ConsoleOut.Block(JsonText.Value(documents));
                return;
            }

            var columns = rows.SelectMany(row => row.Keys).Distinct().ToList();
            var typed = rows.Select(row => (IReadOnlyDictionary<string, object?>)row).ToList();
            TextOut.Table(columns, typed);
            return;
        }

        ConsoleOut.Block(JsonText.Value(documents));
    }

    /// <summary>BSON 值转成与其它连接器一致的输出形态（JSON 里能直接表达的类型）。</summary>
    private static object? Plain(BsonValue value) => value switch
    {
        null => null,
        BsonNull => null,
        BsonBoolean flag => flag.Value,
        BsonInt32 number => number.Value,
        BsonInt64 number => number.Value,
        BsonDouble number => number.Value,
        BsonString text => text.Value,
        BsonObjectId id => id.ToString(),
        BsonDateTime time => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        BsonBinaryData binary => $"<BLOB {binary.Bytes.Length} bytes>",
        BsonArray array => array.Select(Plain).ToArray(),
        BsonDocument document => document.Elements.ToDictionary(element => element.Name, element => Plain(element.Value), StringComparer.Ordinal),
        BsonDecimal128 decimal128 => decimal128.ToString(),
        _ => value.ToString(),
    };

    private static int Connect(string[] args)
    {
        if (!TryParseArgs(args, out var parsed, out var error))
        {
            ConsoleOut.Error(error);
            return 1;
        }

        if (parsed.Positionals.Count < 2)
        {
            ConsoleOut.Error("用法: db-mongo connect <host> <database> [-p port] [-u user] [--password pw]");
            return 1;
        }

        var endpoint = Endpoint.From(parsed);

        try
        {
            ConsoleOut.Line($"正在连接到 MongoDB: {endpoint.Host}:{endpoint.Port}...");

            using var client = Connect(endpoint);
            var database = client.GetDatabase(endpoint.Database);

            var ping = database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            ConsoleOut.Line("✓ 连接成功！");

            var build = database.RunCommand<BsonDocument>(new BsonDocument("buildInfo", 1));
            ConsoleOut.Line($"MongoDB 版本: {build.GetValue("version", "unknown")}");

            var collections = database.ListCollectionNames().ToList();
            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line($"数据库 \"{endpoint.Database}\" 中的集合:");

            if (collections.Count == 0)
            {
                ConsoleOut.Line("  (空数据库)");
            }
            else
            {
                foreach (var name in collections)
                {
                    ConsoleOut.Line($"  - {name}");
                }
            }

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("连接信息:");
            ConsoleOut.Line($"  主机: {endpoint.Host}");
            ConsoleOut.Line($"  端口: {endpoint.Port}");
            ConsoleOut.Line($"  数据库: {endpoint.Database}");
            ConsoleOut.Line($"  用户: {(endpoint.User.Length > 0 ? endpoint.User : "无")}");
            ConsoleOut.Line($"  ping: {ping.GetValue("ok", 0)}");

            ConsoleOut.Line(string.Empty);
            ConsoleOut.Line("连接已关闭。");
            return 0;
        }
        catch (Exception exception)
        {
            ConsoleOut.Error($"✗ 连接失败: {exception.Message}");
            return 1;
        }
    }

    private static bool TryParseArgs(string[] args, out CliArgs parsed, out string error)
    {
        try
        {
            parsed = CliArgs.Parse(args, QueryOptions);
            error = string.Empty;
            return true;
        }
        catch (UsageException exception)
        {
            parsed = null!;
            error = exception.Message;
            return false;
        }
    }

    private static MongoClient Connect(Endpoint endpoint)
    {
        var credentials = endpoint.User.Length > 0
            ? $"{endpoint.User}:{Uri.EscapeDataString(endpoint.Password)}@"
            : string.Empty;

        var settings = MongoClientSettings.FromConnectionString(
            $"mongodb://{credentials}{endpoint.Host}:{endpoint.Port}/?authSource=admin");

        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);

        return new MongoClient(settings);
    }

    internal sealed record Endpoint(string Host, string Database, string User, string Password, int Port)
    {
        public static Endpoint From(CliArgs parsed)
        {
            var port = 27017;
            var portText = parsed.Get("-p");

            if (portText is not null &&
                int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
            {
                port = parsedPort;
            }

            return new Endpoint(
                parsed.Positionals[0],
                parsed.Positionals[1],
                parsed.Get("-u") ?? string.Empty,
                parsed.Get("--password") ?? string.Empty,
                port);
        }
    }
}
