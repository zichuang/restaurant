namespace Restaurant.Core.Domain;

/// <summary>
/// 订单状态。用字符串常量而非 enum，因为数据库存的是小写下划线文本，
/// 省掉一层 enum↔string 转换，调试时日志里直接可读。
/// 取值与 docs/02-data-model.md 第二节严格一致。
/// </summary>
public static class OrderStatus
{
    public const string PendingPay = "pending_pay";
    public const string Paid = "paid";
    public const string Accepted = "accepted";
    public const string Served = "served";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Refunding = "refunding";
    public const string Refunded = "refunded";
}

public static class TableStatus
{
    public const string Idle = "idle";
    public const string Occupied = "occupied";
    public const string Disabled = "disabled";
}

public static class SessionStatus
{
    public const string Open = "open";
    public const string Closed = "closed";
}

public static class PaymentStatus
{
    public const string Created = "created";
    public const string Success = "success";
    public const string Closed = "closed";
    public const string Refunded = "refunded";
}

public static class DishStatus
{
    public const string On = "on";
    public const string Off = "off";
    public const string Deleted = "deleted";
}

public static class StaffRole
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Waiter = "waiter";
    public const string Kitchen = "kitchen";
}
