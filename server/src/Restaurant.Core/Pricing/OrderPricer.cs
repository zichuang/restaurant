using Restaurant.Core.Abstractions;
using Restaurant.Core.Domain;

namespace Restaurant.Core.Pricing;

/// <summary>
/// 服务端算价。纯函数，不碰数据库——菜品字典由调用方查好后传入，
/// 所以这段逻辑可以被单元测试完整覆盖（见 tests/Restaurant.Tests/OrderPricerTests.cs）。
///
/// 三条铁律：
///   1. 客户端传的价格一个都不用，全部按 dishId 重查
///   2. 沽清 / 下架的菜一律拒绝，不静默跳过（否则顾客会以为点了）
///   3. 规则校验失败返回 Result.Fail，不抛异常
/// </summary>
public static class OrderPricer
{
    public const int MaxQtyPerItem = 99;

    public static Result<PricedOrder> Price(
        IReadOnlyList<CartItemInput> cart,
        IReadOnlyDictionary<ulong, Dish> dishes,
        long orderLimitCents = long.MaxValue)
    {
        if (cart is null || cart.Count == 0)
        {
            return Result.Fail<PricedOrder>("cart_empty", "购物车是空的");
        }

        var items = new List<PricedItem>(cart.Count);

        foreach (var line in cart)
        {
            if (line.Qty <= 0)
            {
                return Result.Fail<PricedOrder>("qty_invalid", "菜品数量必须大于 0");
            }

            if (line.Qty > MaxQtyPerItem)
            {
                return Result.Fail<PricedOrder>("qty_invalid", $"单个菜品最多点 {MaxQtyPerItem} 份");
            }

            if (!dishes.TryGetValue(line.DishId, out var dish))
            {
                return Result.Fail<PricedOrder>("dish_not_found", "菜品已不存在，请刷新菜单");
            }

            if (!dish.IsAvailable)
            {
                return Result.Fail<PricedOrder>("dish_unavailable", $"「{dish.Name}」已售完或已下架");
            }

            var specResult = ResolveSpecs(dish, line.Specs);
            if (!specResult.IsSuccess)
            {
                return Result.Fail<PricedOrder>(specResult.ErrorCode!, specResult.ErrorMessage!);
            }

            var addonResult = ResolveAddons(dish, line.Addons);
            if (!addonResult.IsSuccess)
            {
                return Result.Fail<PricedOrder>(addonResult.ErrorCode!, addonResult.ErrorMessage!);
            }

            var (specText, specDelta) = specResult.Value!;
            var (addonsText, addonDelta) = addonResult.Value!;

            // 规格与加料只可能加价，减价会让「明码标价」变得可疑，直接按 0 兜底
            long unitPrice = dish.PriceCents + Math.Max(0, specDelta) + Math.Max(0, addonDelta);

            items.Add(new PricedItem
            {
                DishId = dish.Id,
                DishName = dish.Name,
                ImageUrl = dish.ImageUrl,
                SpecText = specText,
                AddonsText = addonsText,
                UnitPriceCents = unitPrice,
                Qty = line.Qty,
                AmountCents = unitPrice * line.Qty,
                Remark = Trim(line.Remark, 100),
            });
        }

        long total = items.Sum(i => i.AmountCents);
        int count = items.Sum(i => i.Qty);

        if (total <= 0)
        {
            return Result.Fail<PricedOrder>("order_amount_invalid", "订单金额异常，请刷新后重试");
        }

        if (total > orderLimitCents)
        {
            return Result.Fail<PricedOrder>("order_limit_exceeded",
                $"单笔订单不能超过 {orderLimitCents / 100m:0.##} 元，请分次下单");
        }

        return Result.Ok(new PricedOrder
        {
            Items = items,
            TotalCents = total,
            ItemsCount = count,
        });
    }

    /// <summary>
    /// 校验并解析规格：每组必选且单选，选项必须存在。
    /// 返回 (展示文本, 总加价)。
    /// </summary>
    private static Result<(string Text, long DeltaCents)> ResolveSpecs(
        Dish dish, Dictionary<string, string> selections)
    {
        if (dish.Specs.Count == 0)
        {
            return Result.Ok(("", 0L));
        }

        var parts = new List<string>(dish.Specs.Count);
        long delta = 0;

        foreach (var group in dish.Specs)
        {
            if (!selections.TryGetValue(group.Name, out var picked) || string.IsNullOrWhiteSpace(picked))
            {
                return Result.Fail<(string, long)>("spec_required", $"请选择「{group.Name}」");
            }

            var option = group.Options.FirstOrDefault(o => o.Name == picked);
            if (option is null)
            {
                return Result.Fail<(string, long)>("spec_invalid",
                    $"「{group.Name}」没有「{picked}」这个选项");
            }

            // 多组规格拼成「大份 / 中辣」，落库后打印小票直接可读
            parts.Add(option.Name);
            delta += option.DeltaCents;
        }

        return Result.Ok((string.Join(" / ", parts), delta));
    }

    /// <summary>
    /// 校验并解析加料：必须存在，不允许重复（除非该项标了 Multiple）。
    /// </summary>
    private static Result<(string Text, long DeltaCents)> ResolveAddons(
        Dish dish, List<string> picked)
    {
        if (picked is null || picked.Count == 0)
        {
            return Result.Ok(("", 0L));
        }

        var parts = new List<string>(picked.Count);
        long delta = 0;

        foreach (var name in picked)
        {
            var addon = dish.Addons.FirstOrDefault(a => a.Name == name);
            if (addon is null)
            {
                return Result.Fail<(string, long)>("addon_invalid", $"「{dish.Name}」不支持加「{name}」");
            }

            if (!addon.Multiple && parts.Contains(addon.Name))
            {
                return Result.Fail<(string, long)>("addon_invalid", $"「{addon.Name}」只能加一次");
            }

            parts.Add(addon.Name);
            delta += addon.DeltaCents;
        }

        return Result.Ok((string.Join("、", parts), delta));
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var s = value.Trim();
        return s.Length <= max ? s : s[..max];
    }
}
