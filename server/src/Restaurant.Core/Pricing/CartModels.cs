namespace Restaurant.Core.Pricing;

/// <summary>
/// 购物车单项输入。<b>价格与菜品名一律由服务端按 dishId 重新查库计算，
/// 客户端传来的价格一概不采信</b>——这是防篡改的底线。
/// </summary>
public sealed class CartItemInput
{
    public ulong DishId { get; set; }

    public int Qty { get; set; }

    /// <summary>规格选择：组名 → 选项名，如 { "规格": "大份", "辣度": "中辣" }。</summary>
    public Dictionary<string, string> Specs { get; set; } = [];

    /// <summary>加料名称列表，如 [ "加香菜" ]。</summary>
    public List<string> Addons { get; set; } = [];

    public string Remark { get; set; } = "";
}

/// <summary>算价后的明细，字段与 order_items 表一一对应，可直接落库。</summary>
public sealed class PricedItem
{
    public ulong DishId { get; set; }

    public string DishName { get; set; } = "";

    public string ImageUrl { get; set; } = "";

    /// <summary>规格文本快照，如「大份 / 中辣」。</summary>
    public string SpecText { get; set; } = "";

    /// <summary>加料文本快照，如「加香菜、加豆芽」。</summary>
    public string AddonsText { get; set; } = "";

    /// <summary>单价（含规格与加料加价），单位：分。</summary>
    public long UnitPriceCents { get; set; }

    public int Qty { get; set; }

    /// <summary>小计 = UnitPriceCents × Qty。</summary>
    public long AmountCents { get; set; }

    public string Remark { get; set; } = "";
}

public sealed class PricedOrder
{
    public List<PricedItem> Items { get; set; } = [];

    public long TotalCents { get; set; }

    /// <summary>菜品总件数（用于「共 N 件」展示）。</summary>
    public int ItemsCount { get; set; }
}
