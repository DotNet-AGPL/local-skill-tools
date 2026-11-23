namespace Connector.Core.Cli;

using Connector.Core.Output;

/// <summary>
/// 预期错误：消息本身就是给用户看的，边界只打印消息，不带异常类型名。
/// 用于「用法不对 / 文件不存在 / 格式不支持 / 认证缺失」这类可解释的失败。
/// </summary>
public sealed class UserErrorException : Exception
{
    public UserErrorException(string message) : base(message)
    {
    }

    public UserErrorException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// 所有工具的入口包装：统一异常呈现，保证任何情况下都不会把堆栈直接抛给用户。
///   - UsageException  → 纯提示（参数错误）
///   - UserErrorException → ✗ 消息
///   - 其它异常 → ✗ 类型: 消息（保留类型便于排查驱动/网络问题）
/// </summary>
public static class CliHost
{
    public static int Run(Func<int> body)
    {
        try
        {
            return body();
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            return 1;
        }
        catch (UserErrorException error)
        {
            ConsoleOut.Error($"✗ {error.Message}");
            return 1;
        }
        catch (Exception error)
        {
            ConsoleOut.Error(Describe(error));
            return 1;
        }
        finally
        {
            ConsoleOut.Flush();
        }
    }

    /// <summary>给已经在内层 catch 的工具复用同一套呈现规则。</summary>
    public static string Describe(Exception error) =>
        error is UserErrorException ? $"✗ {error.Message}" : $"✗ {error.GetType().Name}: {error.Message}";

    /// <summary>异步入口（目前只有 PostgreSQL 连接器需要）。</summary>
    public static async Task<int> RunAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (UsageException error)
        {
            ConsoleOut.Error(error.Message);
            return 1;
        }
        catch (UserErrorException error)
        {
            ConsoleOut.Error($"✗ {error.Message}");
            return 1;
        }
        catch (Exception error)
        {
            ConsoleOut.Error(Describe(error));
            return 1;
        }
        finally
        {
            ConsoleOut.Flush();
        }
    }
}
