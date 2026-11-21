using Restaurant.Core.Domain;

namespace Restaurant.Core.Abstractions;

/// <summary>
/// 菜单读取。只读——菜单的增删改在管理端接口里，走单独的写仓储。
/// </summary>
public interface IMenuRepository
{
    /// <summary>
    /// 取门店完整菜单：按分类排序，分类内菜品按 sort 排序。
    /// 只返回 status='on' 的分类与菜品；已沽清的菜品仍然返回，但带 SoldOut=true
    /// （顾客要看得到「售完」而不是菜凭空消失）。
    /// </summary>
    Task<IReadOnlyList<Category>> GetMenuAsync(ulong shopId, CancellationToken ct);

    /// <summary>
    /// 按 id 批量取菜品，用于下单时服务端重新算价。
    /// 返回值会包含 status='off' 的菜，由算价逻辑判定是否可售。
    /// </summary>
    Task<IReadOnlyDictionary<ulong, Dish>> GetDishMapAsync(
        ulong shopId, IReadOnlyCollection<ulong> dishIds, CancellationToken ct);
}
