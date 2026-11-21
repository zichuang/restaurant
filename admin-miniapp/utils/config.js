/**
 * 管理端环境配置。与顾客端分开维护：管理端可能部署在不同域名，
 * 且必须强制 HTTPS（生产环境）。
 */
const ENV = 'dev'; // 'dev' | 'prod'

const ENDPOINTS = {
  dev: 'http://localhost:5000',
  prod: 'https://api.your-domain.com',
};

module.exports = {
  ENV,
  BASE_URL: ENDPOINTS[ENV],
  // 实时提醒走 wss，本地是 ws
  WS_URL: ENV === 'dev' ? 'ws://localhost:5000/ws' : 'wss://api.your-domain.com/ws',
};
