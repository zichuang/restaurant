namespace Restaurant.Core.Domain;

/// <summary>
/// 菜品规格组，如「规格：小份/大份」「辣度：微辣/中辣/特辣」。
/// 每组必选且单选，对应 dishes.specs 这个 JSON 列。
/// </summary>
public sealed class SpecGroup
{
    public string Name { get; set; } = "";

    public List<SpecOption> Options { get; set; } = [];
}

public sealed class SpecOption
{
    public string Name { get; set; } = "";

    /// <summary>相对基础价的加价，可为 0。单位：分。</summary>
    public long DeltaCents { get; set; }
}

/// <summary>
/// 加料，如「加香菜」「加豆芽」。可多选（由 Multiple 决定是否允许重复）。
/// </summary>
public sealed class Addon
{
    public string Name { get; set; } = "";

    public long DeltaCents { get; set; }

    /// <summary>是否允许重复选择同一项。</summary>
    public bool Multiple { get; set; }
}

/// <summary>
/// 菜品。name / price_cents 在下单时会快照进 order_items，
/// 所以这里读到的价格只用于「展示与下单当时算价」，不参与历史订单。
/// </summary>
public sealed class Dish
{
    public ulong Id { get; set; }

    public ulong ShopId { get; set; }

    public ulong CategoryId { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string ImageUrl { get; set; } = "";

    /// <summary>基础价，单位：分。</summary>
    public long PriceCents { get; set; }

    public string Unit { get; set; } = "份";

    public List<SpecGroup> Specs { get; set; } = [];

    public List<Addon> Addons { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    /// <summary>估清：临时卖完，当晚可能恢复。</summary>
    public bool SoldOut { get; set; }

    /// <summary>上下架：on / off / deleted。</summary>
    public string Status { get; set; } = DishStatus.On;

    public int Sort { get; set; }

    public int SoldCount { get; set; }

    public bool IsAvailable => Status == DishStatus.On && !SoldOut;

    /// <summary>
    /// 最低可能单价（所有规格都选 0 加价的最便宜项时），用于「起」价展示。
    /// </summary>
    public long MinPriceCents => PriceCents;
}

public sealed class Category
{
    public ulong Id { get; set; }

    public ulong ShopId { get; set; }

    public string Name { get; set; } = "";

    public int Sort { get; set; }

    public string Status { get; set; } = "on";

    public List<Dish> Dishes { get; set; } = [];
}
