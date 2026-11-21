using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restaurant.Core.Abstractions;
using Restaurant.Core.Pricing;
using Restaurant.Core.Serialization;
using Restaurant.Data;
using Restaurant.Data.Repositories;
using Serilog;
using Serilog.Events;

// Serilog 要在 builder 之前初始化，否则启动早期（配置读取、DI 构建）的错误抓不到
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine("logs", "api-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // ---------------------------------------------------------------
    // 1. Dapper 全局设置（必须先于任何查询）
    // ---------------------------------------------------------------
    DapperSetup.Initialize();

    // ---------------------------------------------------------------
    // 2. 配置绑定
    // ---------------------------------------------------------------
    builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
    builder.Services.Configure<ShopOptions>(builder.Configuration.GetSection(ShopOptions.SectionName));

    // ---------------------------------------------------------------
    // 3. JSON 序列化：与 Core 的 AppJson 保持一致（camelCase、中文不转义）
    // ---------------------------------------------------------------
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = AppJson.Default.PropertyNamingPolicy;
        o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.SerializerOptions.Encoder = AppJson.Default.Encoder;
    });

    // ---------------------------------------------------------------
    // 4. 数据层
    // ---------------------------------------------------------------
    builder.Services.AddSingleton<IConnectionFactory>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<DbOptions>>().Value;
        return new MySqlConnectionFactory(opts.ConnectionString);
    });
    builder.Services.AddScoped<IMenuRepository, MenuRepository>();

    // ---------------------------------------------------------------
    // 5. 健康检查
    // ---------------------------------------------------------------
    builder.Services.AddHealthChecks();

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                             | ForwardedHeaders.XForwardedProto
                             | ForwardedHeaders.XForwardedHost;
        // 生产必须收敛到 Nginx 所在 IP；本地开发放开
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    var app = builder.Build();

    // ---------------------------------------------------------------
    // 管道顺序不能随意调整
    // ---------------------------------------------------------------

    // 1) 反代头必须最先处理：走 Nginx 后 Request.Scheme 恒为 http，
    //    不修就会拼出 http:// 的 notify_url，微信支付直接拒。
    app.UseForwardedHeaders();

    // 2) 全局异常兜底：未捕获异常统一 500 + 错误码，不泄漏堆栈
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        var feature = context.Features
            .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        Log.Error(feature?.Error, "未处理异常 {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "internal_error",
            message = "服务暂时不可用，请稍后重试",
        });
    }));

    app.UseSerilogRequestLogging(o =>
    {
        o.MessageTemplate = "{RequestMethod} {RequestPath} → {StatusCode} 耗时 {Elapsed:0.0}ms";
    });

    // ---------------------------------------------------------------
    // 端点
    // ---------------------------------------------------------------
    app.MapGet("/", () => Results.Ok(new
    {
        service = "restaurant-api",
        status = "ok",
        time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)),
    }));

    // 存活探针：不碰数据库，给 Nginx / 监控用
    app.MapHealthChecks("/health");

    // 就绪探针：验证数据库是否真连得上
    app.MapGet("/health/db", async (IConnectionFactory factory, CancellationToken ct) =>
    {
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return Results.Ok(new { db = "ok" });
        }
        catch (Exception ex)
        {
            return Results.Json(new { db = "fail", message = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    // 菜单（M1 第一个真实接口）
    app.MapGet("/api/menu", async (
        IMenuRepository repo,
        IOptions<ShopOptions> shop,
        CancellationToken ct) =>
    {
        var menu = await repo.GetMenuAsync(shop.Value.Id, ct);
        return Results.Ok(new
        {
            shopId = shop.Value.Id,
            categories = menu.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                dishes = c.Dishes.Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    description = d.Description,
                    imageUrl = d.ImageUrl,
                    priceCents = d.PriceCents,
                    unit = d.Unit,
                    soldOut = d.SoldOut,
                    specs = d.Specs,
                    addons = d.Addons,
                    tags = d.Tags,
                }),
            }),
        });
    });

    // 联调用：直接试算价，验证「服务端算价」这条链路
    app.MapPost("/api/pricing/preview", async (
        List<CartItemInput> cart,
        IMenuRepository repo,
        IOptions<ShopOptions> shop,
        CancellationToken ct) =>
    {
        var ids = cart.Select(c => c.DishId).Distinct().ToList();
        var dishMap = await repo.GetDishMapAsync(shop.Value.Id, ids, ct);

        var result = OrderPricer.Price(cart, dishMap, shop.Value.OrderLimitCents);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage });
    });

    Log.Information("restaurant-api 启动完成，环境={Env}", app.Environment.EnvironmentName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "启动失败");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>单店部署期间的店铺参数。做连锁时改成按 AppID / 域名解析。</summary>
public sealed class ShopOptions
{
    public const string SectionName = "Shop";

    public ulong Id { get; set; } = 1;

    /// <summary>单笔订单上限（分），防桌码被盗用后恶意下单。与 shops.order_limit_cents 一致。</summary>
    public long OrderLimitCents { get; set; } = 200_000;
}

/// <summary>供集成测试引用（WebApplicationFactory&lt;Program&gt;）。</summary>
public partial class Program;
