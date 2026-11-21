namespace Restaurant.Core.Domain;

/// <summary>
/// 订单。字段与 orders 表一一对应，所有金额单位为分。
/// 注意 table_no / dish_name 这类快照字段是有意冗余的，不要为了「规范化」去掉。
/// </summary>
public sealed class Order
{
    public ulong Id { get; set; }

    public ulong ShopId { get; set; }

    /// <summary>展示单号，如 01-20261117-0007。</summary>
    public string OrderNo { get; set; } = "";

    public ulong SessionId { get; set; }

    public ulong TableId { get; set; }

    /// <summary>桌号快照。</summary>
    public string TableNo { get; set; } = "";

    /// <summary>会话内第几单，1 是首单，2 起是加菜单。</summary>
    public int Seq { get; set; }

    public string Status { get; set; } = OrderStatus.PendingPay;

    public int ItemsCount { get; set; }

    public long TotalCents { get; set; }

    public long PaidCents { get; set; }

    public long RefundedCents { get; set; }

    public string Remark { get; set; } = "";

    public string GuestOpenId { get; set; } = "";

    /// <summary>下单幂等键，客户端生成，唯一索引兜底。</summary>
    public string ClientToken { get; set; } = "";

    public string CancelReason { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime? ServedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public List<OrderItem> Items { get; set; } = [];
}

/// <summary>订单明细，价格快照表。菜品改价改名不影响这里。</summary>
public sealed class OrderItem
{
    public ulong Id { get; set; }

    public ulong OrderId { get; set; }

    public ulong ShopId { get; set; }

    public ulong DishId { get; set; }

    public string DishName { get; set; } = "";

    public string ImageUrl { get; set; } = "";

    public string SpecText { get; set; } = "";

    public string AddonsText { get; set; } = "";

    public long UnitPriceCents { get; set; }

    public int Qty { get; set; }

    public long AmountCents { get; set; }

    public string Remark { get; set; } = "";

    public string Status { get; set; } = "normal";
}

/// <summary>
/// 订单状态机。所有状态变更都必须先过这里，再走条件更新 SQL：
/// <c>UPDATE orders SET status=? WHERE id=? AND status=?</c>
/// 影响行数为 0 说明状态已被其他请求改掉，此时不要覆盖。
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [OrderStatus.PendingPay] = [OrderStatus.Paid, OrderStatus.Cancelled],
        [OrderStatus.Paid] = [OrderStatus.Accepted, OrderStatus.Refunding],
        [OrderStatus.Accepted] = [OrderStatus.Served],
        [OrderStatus.Served] = [OrderStatus.Completed],
        [OrderStatus.Completed] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Refunding] = [OrderStatus.Refunded, OrderStatus.Paid],
        [OrderStatus.Refunded] = [],
    };

    public static bool CanTransit(string from, string to)
        => Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>终态：不再有任何出边。</summary>
    public static bool IsTerminal(string status)
        => Allowed.TryGetValue(status, out var targets) && targets.Length == 0;

    /// <summary>已支付之后的状态，可用于判断「这单是否收过钱」。</summary>
    public static bool IsPaidLike(string status)
        => status is OrderStatus.Paid or OrderStatus.Accepted or OrderStatus.Served
            or OrderStatus.Completed or OrderStatus.Refunding;
}
