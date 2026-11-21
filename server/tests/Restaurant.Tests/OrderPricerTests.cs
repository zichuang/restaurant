using Restaurant.Core.Domain;
using Restaurant.Core.Pricing;

namespace Restaurant.Tests;

/// <summary>
/// 服务端算价的单元测试。这段逻辑直接决定收多少钱，
/// 每一条拒绝分支都必须有对应用例，否则线上就是「多收了钱」或「少收了钱」。
/// </summary>
public class OrderPricerTests
{
    // ---------- 测试数据 ----------

    private static Dish WaterFish() => new()
    {
        Id = 1,
        ShopId = 1,
        CategoryId = 1,
        Name = "水煮鱼",
        PriceCents = 6800,
        Unit = "份",
        Status = DishStatus.On,
        Specs =
        [
            new SpecGroup
            {
                Name = "规格",
                Options =
                [
                    new SpecOption { Name = "小份", DeltaCents = 0 },
                    new SpecOption { Name = "大份", DeltaCents = 3000 },
                ],
            },
            new SpecGroup
            {
                Name = "辣度",
                Options =
                [
                    new SpecOption { Name = "微辣", DeltaCents = 0 },
                    new SpecOption { Name = "中辣", DeltaCents = 0 },
                    new SpecOption { Name = "特辣", DeltaCents = 0 },
                ],
            },
        ],
        Addons =
        [
            new Addon { Name = "加豆芽", DeltaCents = 500, Multiple = false },
        ],
    };

    private static Dish Rice() => new()
    {
        Id = 12,
        ShopId = 1,
        CategoryId = 5,
        Name = "米饭",
        PriceCents = 300,
        Unit = "碗",
        Status = DishStatus.On,
    };

    private static Dictionary<ulong, Dish> Menu(params Dish[] dishes)
        => dishes.ToDictionary(d => d.Id);

    // ---------- 正常路径 ----------

    [Fact]
    public void 无规格无加料_按单价乘数量算()
    {
        var cart = new List<CartItemInput> { new() { DishId = 12, Qty = 3 } };

        var result = OrderPricer.Price(cart, Menu(Rice()));

        Assert.True(result.IsSuccess);
        Assert.Equal(900, result.Value!.TotalCents);
        Assert.Equal(3, result.Value.ItemsCount);
        Assert.Single(result.Value.Items);
        Assert.Equal(300, result.Value.Items[0].UnitPriceCents);
    }

    [Fact]
    public void 规格加价计入单价()
    {
        var cart = new List<CartItemInput>
        {
            new()
            {
                DishId = 1,
                Qty = 2,
                Specs = new Dictionary<string, string> { ["规格"] = "大份", ["辣度"] = "中辣" },
            },
        };

        var result = OrderPricer.Price(cart, Menu(WaterFish()));

        Assert.True(result.IsSuccess);
        // 6800 + 3000 = 9800，× 2 = 19600
        Assert.Equal(9800, result.Value!.Items[0].UnitPriceCents);
        Assert.Equal(19600, result.Value.TotalCents);
        Assert.Equal("大份 / 中辣", result.Value.Items[0].SpecText);
    }

    [Fact]
    public void 加料加价计入单价并生成快照文本()
    {
        var cart = new List<CartItemInput>
        {
            new()
            {
                DishId = 1,
                Qty = 1,
                Specs = new Dictionary<string, string> { ["规格"] = "小份", ["辣度"] = "微辣" },
                Addons = ["加豆芽"],
            },
        };

        var result = OrderPricer.Price(cart, Menu(WaterFish()));

        Assert.True(result.IsSuccess);
        // 6800 + 0 + 500 = 7300
        Assert.Equal(7300, result.Value!.TotalCents);
        Assert.Equal("加豆芽", result.Value.Items[0].AddonsText);
    }

    [Fact]
    public void 多项菜品汇总()
    {
        var cart = new List<CartItemInput>
        {
            new()
            {
                DishId = 1,
                Qty = 1,
                Specs = new Dictionary<string, string> { ["规格"] = "小份", ["辣度"] = "微辣" },
            },
            new() { DishId = 12, Qty = 2 },
        };

        var result = OrderPricer.Price(cart, Menu(WaterFish(), Rice()));

        Assert.True(result.IsSuccess);
        Assert.Equal(6800 + 600, result.Value!.TotalCents);
        Assert.Equal(3, result.Value.ItemsCount);
        Assert.Equal(2, result.Value.Items.Count);
    }

    // ---------- 拒绝路径 ----------

    [Fact]
    public void 空购物车被拒()
    {
        var result = OrderPricer.Price([], Menu(Rice()));

        Assert.False(result.IsSuccess);
        Assert.Equal("cart_empty", result.ErrorCode);
    }

    [Fact]
    public void 数量为零被拒()
    {
        var cart = new List<CartItemInput> { new() { DishId = 12, Qty = 0 } };

        var result = OrderPricer.Price(cart, Menu(Rice()));

        Assert.False(result.IsSuccess);
        Assert.Equal("qty_invalid", result.ErrorCode);
    }

    [Fact]
    public void 数量超上限被拒()
    {
        var cart = new List<CartItemInput> { new() { DishId = 12, Qty = OrderPricer.MaxQtyPerItem + 1 } };

        var result = OrderPricer.Price(cart, Menu(Rice()));

        Assert.False(result.IsSuccess);
        Assert.Equal("qty_invalid", result.ErrorCode);
    }

    [Fact]
    public void 菜品不存在被拒()
    {
        var cart = new List<CartItemInput> { new() { DishId = 999, Qty = 1 } };

        var result = OrderPricer.Price(cart, Menu(Rice()));

        Assert.False(result.IsSuccess);
        Assert.Equal("dish_not_found", result.ErrorCode);
    }

    [Fact]
    public void 沽清的菜被拒_不静默跳过()
    {
        var dish = Rice();
        dish.SoldOut = true;

        var cart = new List<CartItemInput> { new() { DishId = 12, Qty = 1 } };

        var result = OrderPricer.Price(cart, Menu(dish));

        Assert.False(result.IsSuccess);
        Assert.Equal("dish_unavailable", result.ErrorCode);
        Assert.Contains("米饭", result.ErrorMessage);
    }

    [Fact]
    public void 已下架的菜被拒()
    {
        var dish = Rice();
        dish.Status = DishStatus.Off;

        var cart = new List<CartItemInput> { new() { DishId = 12, Qty = 1 } };

        var result = OrderPricer.Price(cart, Menu(dish));

        Assert.False(result.IsSuccess);
        Assert.Equal("dish_unavailable", result.ErrorCode);
    }

    [Fact]
    public void 必选规格没选被拒()
    {
        var cart = new List<CartItemInput> { new() { DishId = 1, Qty = 1 } };

        var result = OrderPricer.Price(cart, Menu(WaterFish()));

        Assert.False(result.IsSuccess);
        Assert.Equal("spec_required", result.ErrorCode);
    }

    [Fact]
    public void 只选了部分规格组被拒()
    {
        var cart = new List<CartItemInput>
        {
            new()
            {
                DishId = 1,
                Qty = 1,
                Specs = new Dictionary<string, string> { ["规格"] = "大份" }, // 缺辣度
            },
        };

        var result = OrderPricer.Price(cart, Menu(WaterFish()));

        Assert.False(result.IsSuccess);
        Assert.Equal("spec_required", result.ErrorCode);
        Assert.Contains("辣度", result.ErrorMessage);
    }

    [Fact]
    public void 规格选项不存在被拒()
    {
        var cart = new List<CartItemInput>
        {
            new()
            {
                DishId = 1,
                Qty = 1,
                Specs = new Dictionary<string, string> { ["规格"] = "超大份", ["辣度"] = "微辣" },
            },
        };

        var result = OrderPricer.Price(cart, Menu(WaterFish()));

        Assert.False(result.IsSuccess);
        Assert.Equal("spec_invalid", result.ErrorCode);
    }

    [Fact]
    public void 不支持的加料被拒()
    {
        var cart = new List<CartItemInput>
        {
            new()
            {
                DishId = 1,
                Qty = 1,
                Specs = new Dictionary<string, string> { ["规格"] = "小份", ["辣度"] = "微辣" },
                Addons = ["加鲍鱼"],
            },
        };

        var result = OrderPricer.Price(cart, Menu(WaterFish()));

        Assert.False(result.IsSuccess);
        Assert.Equal("addon_invalid", result.ErrorCode);
    }

    [Fact]
    public void 不允许重复的加料重复选被拒()
    {
        var cart = new List<CartItemInput>
        {
            new()
            {
                DishId = 1,
                Qty = 1,
                Specs = new Dictionary<string, string> { ["规格"] = "小份", ["辣度"] = "微辣" },
                Addons = ["加豆芽", "加豆芽"],
            },
        };

        var result = OrderPricer.Price(cart, Menu(WaterFish()));

        Assert.False(result.IsSuccess);
        Assert.Equal("addon_invalid", result.ErrorCode);
    }

    [Fact]
    public void 超过单笔限额被拒()
    {
        var cart = new List<CartItemInput> { new() { DishId = 1, Qty = 2,
            Specs = new Dictionary<string, string> { ["规格"] = "大份", ["辣度"] = "中辣" } } };

        // 19600 分 = 196 元，限额设 100 元
        var result = OrderPricer.Price(cart, Menu(WaterFish()), orderLimitCents: 10_000);

        Assert.False(result.IsSuccess);
        Assert.Equal("order_limit_exceeded", result.ErrorCode);
    }

    [Fact]
    public void 备注超长被截断到100字符()
    {
        var cart = new List<CartItemInput>
        {
            new() { DishId = 12, Qty = 1, Remark = new string('辣', 300) },
        };

        var result = OrderPricer.Price(cart, Menu(Rice()));

        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value!.Items[0].Remark.Length);
    }
}
