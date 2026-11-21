const { BASE_URL } = require('./utils/config');

App({
  globalData: {
    baseUrl: BASE_URL,
    // 扫码进入后由 pages/entry 写入，后续所有请求都用它
    tableNo: '',
    sessionId: 0,
    shopId: 1,
    // 顾客身份：真实项目里由 wx.login 换 openid，这里留出位置
    openid: '',
    cart: [],
  },

  onLaunch() {
    console.log('[app] onLaunch，baseUrl =', BASE_URL);
  },

  onShow() {
    console.log('[app] onShow');
  },

  onError(err) {
    // 小程序崩溃不会弹窗，只在这里留痕，便于真机排查
    console.error('[app] onError', err);
  },
});
