const api = require('../../utils/api');
const app = getApp();

/** 分 → 元的展示文本：2800 → "28"，2850 → "28.5" */
function toYuan(cents) {
  const v = (cents || 0) / 100;
  return Number.isInteger(v) ? String(v) : v.toFixed(2).replace(/0+$/, '');
}

Page({
  data: {
    tableNo: '',
    zone: '',

    loading: true,
    loadError: '',

    categories: [],
    activeCatId: 0,
    activeCatName: '',
    visibleDishes: [],

    cartCount: 0,
    cartTotalYuan: '0',

    // 规格弹层
    showSpec: false,
    specDish: null,
    specPick: {},
    addonPick: {},
    specRemark: '',
    specTotalYuan: '0',
  },

  onLoad(options) {
    const tableNo = options.tableNo
      ? decodeURIComponent(options.tableNo)
      : app.globalData.tableNo;

    this._menu = [];   // 完整菜单，不放进 data（setData 有 1MB 上限，且无必要）
    this._cart = [];   // 购物车

    this.setData({ tableNo: tableNo || '未知桌号' });
    this.loadMenu();
  },

  // ---------------------------------------------------------------
  // 数据加载
  // ---------------------------------------------------------------
  async loadMenu() {
    this.setData({ loading: true, loadError: '' });

    try {
      const res = await api.getMenu(app.globalData.shopId);
      const categories = (res.categories || []).map((c) => ({
        id: c.id,
        name: c.name,
        dishes: (c.dishes || []).map((d) => ({
          ...d,
          hasSpec: (d.specs && d.specs.length > 0) || (d.addons && d.addons.length > 0),
          priceYuan: toYuan(d.priceCents),
          qty: 0,
          specs: d.specs || [],
          addons: d.addons || [],
          tags: d.tags || [],
        })),
      }));

      if (categories.length === 0) {
        this.setData({ loading: false, loadError: '菜单为空，请联系店员' });
        return;
      }

      this._menu = categories;

      this.setData(
        {
          loading: false,
          categories,
          activeCatId: categories[0].id,
          activeCatName: categories[0].name,
        },
        () => this.refreshVisible()
      );
    } catch (err) {
      console.error('[menu] 加载失败', err);
      this.setData({
        loading: false,
        loadError: err.code === 'network_error'
          ? '连不上服务器，请确认后端已启动（localhost:5000）'
          : err.message || '菜单加载失败',
      });
    }
  },

  // ---------------------------------------------------------------
  // 分类切换
  // ---------------------------------------------------------------
  onSwitchCat(e) {
    const id = e.currentTarget.dataset.id;
    const cat = this._menu.find((c) => c.id === id);
    if (!cat) return;

    this.setData({ activeCatId: id, activeCatName: cat.name }, () => this.refreshVisible());
  },

  /** 按当前分类刷新右侧列表，并同步每道菜已选数量 */
  refreshVisible() {
    const cat = this._menu.find((c) => c.id === this.data.activeCatId);
    if (!cat) return;

    const qtyMap = this.buildQtyMap();
    const visible = cat.dishes.map((d) => ({ ...d, qty: qtyMap[d.id] || 0 }));

    this.setData({ visibleDishes: visible });
  },

  /** dishId → 已选总份数（含不同规格） */
  buildQtyMap() {
    const map = {};
    this._cart.forEach((line) => {
      map[line.dishId] = (map[line.dishId] || 0) + line.qty;
    });
    return map;
  },

  // ---------------------------------------------------------------
  // 加减
  // ---------------------------------------------------------------
  onAdd(e) {
    const id = Number(e.currentTarget.dataset.id);
    const dish = this.findDish(id);
    if (!dish) return;

    if (dish.soldOut) {
      wx.showToast({ title: '这道菜已售完', icon: 'none' });
      return;
    }

    // 有规格/加料的必须进弹层选，不能默认加
    if (dish.hasSpec) {
      this.openSpec(dish);
      return;
    }

    this.addToCart({
      dishId: dish.id,
      name: dish.name,
      unitPriceCents: dish.priceCents,
      specText: '',
      addonsText: '',
      specs: {},
      addons: [],
      remark: '',
    });
  },

  onDec(e) {
    const id = Number(e.currentTarget.dataset.id);

    // 从后往前找，减掉最后加入的那一条，符合直觉
    for (let i = this._cart.length - 1; i >= 0; i -= 1) {
      if (this._cart[i].dishId === id) {
        this._cart[i].qty -= 1;
        if (this._cart[i].qty <= 0) {
          this._cart.splice(i, 1);
        }
        break;
      }
    }

    this.syncCart();
  },

  findDish(id) {
    for (const cat of this._menu) {
      const hit = cat.dishes.find((d) => d.id === id);
      if (hit) return hit;
    }
    return null;
  },

  // ---------------------------------------------------------------
  // 购物车
  // ---------------------------------------------------------------
  cartKeyOf(line) {
    const specKey = Object.keys(line.specs).sort().map((k) => `${k}:${line.specs[k]}`).join('|');
    const addonKey = [...line.addons].sort().join(',');
    return `${line.dishId}#${specKey}#${addonKey}#${line.remark || ''}`;
  },

  addToCart(line, qty = 1) {
    const key = this.cartKeyOf(line);
    const exist = this._cart.find((l) => this.cartKeyOf(l) === key);

    if (exist) {
      exist.qty += qty;
    } else {
      this._cart.push({ ...line, key, qty });
    }

    this.syncCart();
  },

  syncCart() {
    const count = this._cart.reduce((s, l) => s + l.qty, 0);
    const total = this._cart.reduce((s, l) => s + l.unitPriceCents * l.qty, 0);

    app.globalData.cart = this._cart;

    this.setData(
      {
        cartCount: count,
        cartTotalYuan: toYuan(total),
      },
      () => this.refreshVisible()
    );
  },

  // ---------------------------------------------------------------
  // 规格弹层
  // ---------------------------------------------------------------
  openSpec(dish) {
    // 默认帮用户选中每组第一个，减少点击（辣度这种有意义，规格最好也让用户主动选）
    const specPick = {};

    this.setData({
      showSpec: true,
      specDish: {
        ...dish,
        specs: dish.specs.map((g) => ({
          ...g,
          options: g.options.map((o) => ({ ...o, deltaYuan: toYuan(o.deltaCents) })),
        })),
        addons: dish.addons.map((a) => ({ ...a, deltaYuan: toYuan(a.deltaCents) })),
      },
      specPick,
      addonPick: {},
      specRemark: '',
      specTotalYuan: toYuan(dish.priceCents),
    });
  },

  closeSpec() {
    this.setData({ showSpec: false, specDish: null });
  },

  onPickSpec(e) {
    const { group, opt } = e.currentTarget.dataset;
    const specPick = { ...this.data.specPick, [group]: opt };

    this.setData({ specPick, specTotalYuan: this.calcSpecTotal(specPick, this.data.addonPick) });
  },

  onToggleAddon(e) {
    const name = e.currentTarget.dataset.name;
    const addonPick = { ...this.data.addonPick };

    if (addonPick[name]) {
      delete addonPick[name];
    } else {
      addonPick[name] = true;
    }

    this.setData({ addonPick, specTotalYuan: this.calcSpecTotal(this.data.specPick, addonPick) });
  },

  onRemarkInput(e) {
    this.setData({ specRemark: e.detail.value });
  },

  calcSpecTotal(specPick, addonPick) {
    const dish = this.data.specDish;
    if (!dish) return '0';

    let cents = dish.priceCents;

    dish.specs.forEach((g) => {
      const picked = specPick[g.name];
      const opt = g.options.find((o) => o.name === picked);
      if (opt) cents += opt.deltaCents || 0;
    });

    dish.addons.forEach((a) => {
      if (addonPick[a.name]) cents += a.deltaCents || 0;
    });

    return toYuan(cents);
  },

  confirmSpec() {
    const dish = this.data.specDish;
    if (!dish) return;

    // 每组规格都必须选，否则服务端会拒（spec_required）——前端先挡一次
    for (const g of dish.specs) {
      if (!this.data.specPick[g.name]) {
        wx.showToast({ title: `请选择「${g.name}」`, icon: 'none' });
        return;
      }
    }

    const specNames = dish.specs.map((g) => this.data.specPick[g.name]).filter(Boolean);
    const addonNames = dish.addons.filter((a) => this.data.addonPick[a.name]).map((a) => a.name);

    let unitPrice = dish.priceCents;
    dish.specs.forEach((g) => {
      const opt = g.options.find((o) => o.name === this.data.specPick[g.name]);
      if (opt) unitPrice += opt.deltaCents || 0;
    });
    dish.addons.forEach((a) => {
      if (this.data.addonPick[a.name]) unitPrice += a.deltaCents || 0;
    });

    this.addToCart({
      dishId: dish.id,
      name: dish.name,
      unitPriceCents: unitPrice,
      specText: specNames.join(' / '),
      addonsText: addonNames.join('、'),
      specs: { ...this.data.specPick },
      addons: addonNames,
      remark: this.data.specRemark,
    });

    this.closeSpec();
  },

  // ---------------------------------------------------------------
  // 结算
  // ---------------------------------------------------------------
  goCheckout() {
    if (this._cart.length === 0) return;

    // M1 骨架阶段：下单与会话接口还没实现，先把真实请求体打到控制台，
    // 便于对照 docs/03-api.md 的 POST /api/orders 契约做下一步。
    const payload = this._cart.map((l) => ({
      dishId: l.dishId,
      qty: l.qty,
      specs: l.specs,
      addons: l.addons,
      remark: l.remark,
    }));

    console.log('[checkout] 待提交订单', JSON.stringify(payload, null, 2));

    wx.showModal({
      title: '还差一步',
      content: '下单与微信支付接口还在开发中。当前购物车内容已打印到控制台，可用于核对算价。',
      showCancel: false,
      confirmText: '知道了',
    });
  },
});
