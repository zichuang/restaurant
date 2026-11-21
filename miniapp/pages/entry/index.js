const app = getApp();

Page({
  data: {
    tableNo: '',
    zone: '',
    seats: 0,
    inputNo: '',
    debug: '',
  },

  /**
   * 扫码进入时微信把小程序码的 scene 放在 options.scene 里，
   * 且是 URL 编码过的，必须先 decodeURIComponent。
   *
   * scene 只有 32 个可见字符的额度，所以桌号用 A01 这种短编号；
   * 若以后要带门店参数，用 k=v&k=v 形式，这里一并支持。
   */
  onLoad(options) {
    let scene = options.scene ? decodeURIComponent(options.scene) : '';

    if (!scene && options.tableNo) {
      scene = options.tableNo;
    }

    if (scene) {
      const params = {};
      scene.split('&').forEach((pair) => {
        const idx = pair.indexOf('=');
        if (idx > 0) {
          params[pair.slice(0, idx)] = pair.slice(idx + 1);
        }
      });
      // 兼容两种写法：scene=A01 与 scene=T=A01
      scene = params.t || params.tableNo || scene.split('&')[0];
    }

    this.setData({ debug: options.scene || '(无)' });

    if (scene) {
      this.applyTable(scene);
    }
  },

  /** 校验并应用桌号。真实项目这里应该调 /api/session/open 校验桌台状态 */
  applyTable(no) {
    const tableNo = String(no).trim().toUpperCase();

    if (!/^[A-Z][0-9]{1,3}$/.test(tableNo)) {
      wx.showToast({ title: '桌号格式不正确', icon: 'none' });
      return;
    }

    // 演示数据里 A01-A04 大厅、B01-B02 包间、C01-C02 露台
    const map = {
      A: { zone: '大厅', seats: 4 },
      B: { zone: '包间', seats: 8 },
      C: { zone: '露台', seats: 4 },
    };
    const meta = map[tableNo[0]] || { zone: '', seats: 0 };

    app.globalData.tableNo = tableNo;
    this.setData({ tableNo, zone: meta.zone, seats: meta.seats });
  },

  onInputNo(e) {
    this.setData({ inputNo: e.detail.value });
  },

  onStart() {
    const tableNo = this.data.tableNo || String(this.data.inputNo).trim().toUpperCase();

    if (!tableNo) {
      wx.showToast({ title: '请先输入桌号', icon: 'none' });
      return;
    }

    if (!this.data.tableNo) {
      this.applyTable(tableNo);
      if (!app.globalData.tableNo) {
        return;
      }
    }

    // 用 redirectTo 而不是 navigateTo：避免用户返回后回到开台页，
    // 那样会重复开台（文档 C1 的约定）
    wx.redirectTo({
      url: `/pages/menu/index?tableNo=${encodeURIComponent(app.globalData.tableNo)}`,
    });
  },
});
