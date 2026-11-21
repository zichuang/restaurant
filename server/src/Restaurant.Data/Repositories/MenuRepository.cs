using Dapper;
using Restaurant.Core.Abstractions;
using Restaurant.Core.Domain;
using Restaurant.Core.Serialization;

namespace Restaurant.Data.Repositories;

/// <summary>
/// 菜单读取。两条查询 + 一次内存分组，不做 N+1。
/// </summary>
public sealed class MenuRepository : IMenuRepository
{
    private const int TimeoutSeconds = 5;

    private readonly IConnectionFactory _factory;

    public MenuRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Category>> GetMenuAsync(ulong shopId, CancellationToken ct)
    {
        // 显式列名，禁止 SELECT *（见 02-data-model.md 9.2）
        const string catSql = """
            SELECT id, shop_id, name, sort, status
              FROM categories
             WHERE shop_id = @shopId AND status = 'on'
             ORDER BY sort, id
            """;

        const string dishSql = """
            SELECT id, shop_id, category_id, name, description, image_url,
                   price_cents, unit, specs, addons, tags,
                   sold_out, status, sort, sold_count
              FROM dishes
             WHERE shop_id = @shopId AND status = 'on'
             ORDER BY category_id, sort, id
            """;

        await using var conn = await _factory.OpenAsync(ct);

        var catCmd = new CommandDefinition(catSql, new { shopId }, commandTimeout: TimeoutSeconds,
            cancellationToken: ct);
        var dishCmd = new CommandDefinition(dishSql, new { shopId }, commandTimeout: TimeoutSeconds,
            cancellationToken: ct);

        var categories = (await conn.QueryAsync<CategoryRow>(catCmd)).ToList();
        var dishes = await conn.QueryAsync<DishRow>(dishCmd);

        var byCategory = dishes
            .GroupBy(d => d.CategoryId)
            .ToDictionary(g => g.Key, g => g.Select(ToDish).ToList());

        return categories.Select(c => new Category
        {
            Id = c.Id,
            ShopId = c.ShopId,
            Name = c.Name,
            Sort = c.Sort,
            Status = c.Status,
            Dishes = byCategory.TryGetValue(c.Id, out var list) ? list : [],
        }).ToList();
    }

    public async Task<IReadOnlyDictionary<ulong, Dish>> GetDishMapAsync(
        ulong shopId, IReadOnlyCollection<ulong> dishIds, CancellationToken ct)
    {
        if (dishIds is null || dishIds.Count == 0)
        {
            return new Dictionary<ulong, Dish>();
        }

        // 不过滤 status：下单时要能区分「不存在」和「已下架」，好给出准确提示
        const string sql = """
            SELECT id, shop_id, category_id, name, description, image_url,
                   price_cents, unit, specs, addons, tags,
                   sold_out, status, sort, sold_count
              FROM dishes
             WHERE shop_id = @shopId AND id IN @ids AND status <> 'deleted'
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var cmd = new CommandDefinition(sql, new { shopId, ids = dishIds }, commandTimeout: TimeoutSeconds,
            cancellationToken: ct);

        var rows = await conn.QueryAsync<DishRow>(cmd);
        return rows.Select(ToDish).ToDictionary(d => d.Id);
    }

    private static Dish ToDish(DishRow r) => new()
    {
        Id = r.Id,
        ShopId = r.ShopId,
        CategoryId = r.CategoryId,
        Name = r.Name,
        Description = r.Description,
        ImageUrl = r.ImageUrl,
        PriceCents = r.PriceCents,
        Unit = r.Unit,
        // JSON 列在 C# 侧是 string，反序列化统一走 AppJson
        Specs = AppJson.DeserializeList<SpecGroup>(r.Specs),
        Addons = AppJson.DeserializeList<Addon>(r.Addons),
        Tags = AppJson.DeserializeList<string>(r.Tags),
        SoldOut = r.SoldOut,
        Status = r.Status,
        Sort = r.Sort,
        SoldCount = r.SoldCount,
    };

    // ---- 行模型：字段名与列名靠 MatchNamesWithUnderscores 自动对应 ----

    private sealed class CategoryRow
    {
        public ulong Id { get; init; }
        public ulong ShopId { get; init; }
        public string Name { get; init; } = "";
        public int Sort { get; init; }
        public string Status { get; init; } = "";
    }

    private sealed class DishRow
    {
        public ulong Id { get; init; }
        public ulong ShopId { get; init; }
        public ulong CategoryId { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string ImageUrl { get; init; } = "";
        public long PriceCents { get; init; }
        public string Unit { get; init; } = "";
        public string? Specs { get; init; }
        public string? Addons { get; init; }
        public string? Tags { get; init; }
        public bool SoldOut { get; init; }
        public string Status { get; init; } = "";
        public int Sort { get; init; }
        public int SoldCount { get; init; }
    }
}
