using MySqlConnector;

namespace Restaurant.Data;

public sealed class DbOptions
{
    public const string SectionName = "Db";

    /// <summary>
    /// MySQL 连接串。本地开发放在 appsettings.Development.json（已 gitignore），
    /// 生产放在 appsettings.Production.json 或环境变量 Db__ConnectionString。
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>单条命令超时（秒）。02-data-model.md 5.4 约定 5 秒。</summary>
    public int CommandTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// 连接工厂。用 MySqlConnector 的连接池（默认 MinimumPoolSize=0, MaximumPoolSize=100），
/// 不要自己缓存 MySqlConnection——连接不是线程安全的，池化由驱动负责。
/// </summary>
public interface IConnectionFactory
{
    Task<MySqlConnection> OpenAsync(CancellationToken ct);
}

public sealed class MySqlConnectionFactory : IConnectionFactory
{
    private readonly string _connectionString;

    public MySqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "数据库连接串未配置。请在 appsettings.Development.json 的 Db:ConnectionString 里填写，" +
                "或设置环境变量 Db__ConnectionString。");
        }

        _connectionString = connectionString;
    }

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
