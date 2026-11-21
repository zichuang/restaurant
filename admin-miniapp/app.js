const { BASE_URL } = require('./utils/config');

App({
  globalData: {
    baseUrl: BASE_URL,
    shopId: 1,
    // 员工身份：由 /api/staff/login 换取，M2 接入
    staff: null,
    // WebSocket 实例：新单提醒靠它，M2 接入
    socket: null,
  },

  onLaunch() {
    console.log('[admin] onLaunch，baseUrl =', BASE_URL);
  },
});
