const app = getApp();

/** 分 → 元 */
function toYuan(cents) {
  const v = (cents || 0) / 100;
  return Number.isInteger(v) ? String(v) : v.toFixed(2).replace(/0+$/, '');
}

/**
 * 桌况页。
 *
 * 当前用演示数据渲染，目的是把「网格布局 + 状态配色 + 待接单红点」
 * 这套交互先定下来。接入真实数据时只改 loadTables()，WXML 不用动。
 */
Page({
  data: {
    tables: [],
    stat: { idle: 0, occupied: 0, pending: 0, total: 0 },
  },

  onLoad() {
    this.loadTables();
  },

  onShow() {
    // TODO(M2)：这里订阅 WebSocket，新单到达时播声音 + 震动，
    //           并用 wx.setKeepScreenOn(true) 保持常亮。
    //           注意只能在页面栈存在时生效，退到后台就收不到。
  },

  loadTables() {
    const raw = [
      { id: 1, no: 'A01', zone: '大厅', seats: 4, status: 'occupied', amountCents: 18600, minutes: 32, orders: 2, pending: 1 },
      { id: 2, no: 'A02', zone: '大厅', seats: 4, status: 'occupied', amountCents: 7400, minutes: 12, orders: 1, pending: 0 },
      { id: 3, no: 'A03', zone: '大厅', seats: 2, status: 'idle', amountCents: 0, minutes: 0, orders: 0, pending: 0 },
      { id: 4, no: 'A04', zone: '大厅', seats: 6, status: 'idle', amountCents: 0, minutes: 0, orders: 0, pending: 0 },
      { id: 5, no: 'B01', zone: '包间', seats: 10, status: 'occupied', amountCents: 45800, minutes: 61, orders: 3, pending: 0 },
      { id: 6, no: 'B02', zone: '包间', seats: 8, status: 'idle', amountCents: 0, minutes: 0, orders: 0, pending: 0 },
      { id: 7, no: 'C01', zone: '露台', seats: 4, status: 'occupied', amountCents: 3200, minutes: 5, orders: 1, pending: 1 },
      { id: 8, no: 'C02', zone: '露台', seats: 4, status: 'idle', amountCents: 0, minutes: 0, orders: 0, pending: 0 },
    ];

    const tables = raw.map((t) => ({
      ...t,
      amountYuan: toYuan(t.amountCents),
    }));

    const stat = {
      idle: tables.filter((t) => t.status === 'idle').length,
      occupied: tables.filter((t) => t.status === 'occupied').length,
      pending: tables.reduce((s, t) => s + (t.pending || 0), 0),
      total: tables.length,
    };

    this.setData({ tables, stat });
  },
});
