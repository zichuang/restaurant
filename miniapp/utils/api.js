const { BASE_URL } = require('./config');

/**
 * wx.request 的 Promise 封装。
 *
 * 约定（与 docs/03-api.md 一致）：
 *   成功：HTTP 2xx，resolve(body)
 *   业务失败：HTTP 4xx 且 body 为 { code, message }，reject(Error{code, message})
 *   网络失败：reject(Error{code:'network_error'})
 *
 * 注意：小程序里 wx.request 不是 Promise，回调风格，且错误分支只有 fail 一种，
 * HTTP 4xx/5xx 都会进 success 回调——这是新人最容易踩的坑。
 */
function request(path, { method = 'GET', data = null, timeout = 10000 } = {}) {
  return new Promise((resolve, reject) => {
    wx.request({
      url: BASE_URL + path,
      method,
      data,
      timeout,
      header: { 'content-type': 'application/json' },
      success(res) {
        if (res.statusCode >= 200 && res.statusCode < 300) {
          resolve(res.data);
          return;
        }
        const body = res.data || {};
        const err = new Error(body.message || `请求失败（${res.statusCode}）`);
        err.code = body.code || `http_${res.statusCode}`;
        err.statusCode = res.statusCode;
        reject(err);
      },
      fail(err) {
        const e = new Error('网络不可用，请检查网络后重试');
        e.code = 'network_error';
        e.detail = err;
        reject(e);
      },
    });
  });
}

/** 取菜单：分类 + 菜品，已沽清的菜带 soldOut=true */
function getMenu(shopId = 1) {
  return request(`/api/menu?shopId=${shopId}`);
}

/** 服务端算价预览（M1 联调用，正式下单走 POST /api/orders） */
function previewPrice(cart) {
  return request('/api/pricing/preview', {
    method: 'POST',
    data: cart.map((line) => ({
      dishId: line.dishId,
      qty: line.qty,
      specs: line.specs || {},
      addons: line.addons || [],
      remark: line.remark || '',
    })),
  });
}

module.exports = { request, getMenu, previewPrice };
