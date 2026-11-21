using System.Data;
using Dapper;

namespace Restaurant.Data;

/// <summary>
/// Dapper 全局设置。必须在应用启动时（注册任何查询之前）调用一次。
/// 见 docs/02-data-model.md 9.2 / 9.3。
/// </summary>
public static class DapperSetup
{
    private static int _initialized;

    public static void Initialize()
    {
        // 幂等：测试宿主可能重复调用
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        // 1. order_no -> OrderNo。不设这一条，查询会静默返回 null / 0，
        //    编译期没有任何提示，是 Dapper 最容易踩的坑。
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // 2. DATETIME 读出来是 Unspecified，必须显式标成 UTC，
        //    否则 System.Text.Json 不加 Z，小程序按本地时区解析会晚 8 小时。
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
        SqlMapper.AddTypeHandler(new NullableUtcDateTimeHandler());
    }
}

/// <summary>DATETIME(3) → DateTime（Kind=Utc）。</summary>
public sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override DateTime Parse(object value) => value switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        string s => DateTime.SpecifyKind(DateTime.Parse(s), DateTimeKind.Utc),
        _ => throw new DataException($"无法把 {value?.GetType().Name} 转成 DateTime"),
    };

    public override void SetValue(IDbDataParameter parameter, DateTime value)
    {
        parameter.DbType = DbType.DateTime;
        parameter.Value = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}

public sealed class NullableUtcDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
{
    public override DateTime? Parse(object value)
        => value is null or DBNull ? null : new UtcDateTimeHandler().Parse(value);

    public override void SetValue(IDbDataParameter parameter, DateTime? value)
    {
        parameter.DbType = DbType.DateTime;
        parameter.Value = value is null
            ? DBNull.Value
            : (value.Value.Kind == DateTimeKind.Utc ? value.Value : value.Value.ToUniversalTime());
    }
}
