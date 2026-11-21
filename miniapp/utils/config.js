/**
 * 环境配置。
 *
 * 本地开发：BASE_URL 指向本机 ASP.NET Core（http://localhost:5000）。
 *   - 模拟器里 localhost 就是本机，直接可用
 *   - 真机预览时手机访问不到电脑的 localhost，要改成电脑的局域网 IP，
 *     并把 launchSettings 的 profile 换成 http-lan（监听 0.0.0.0）
 *   - 开发者工具里必须勾选「不校验合法域名」，project.config.json 已设 urlCheck:false
 *
 * 生产：换成备案域名，且必须在微信后台「开发管理 → 服务器域名」里加白名单。
 */

const ENV = 'dev'; // 'dev' | 'prod'

const ENDPOINTS = {
  dev: 'http://localhost:5000',
  // 真机联调示例：'http://192.168.1.100:5000'
  prod: 'https://api.your-domain.com',
};

module.exports = {
  ENV,
  BASE_URL: ENDPOINTS[ENV],
};
