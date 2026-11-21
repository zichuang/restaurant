# 餐厅扫码点菜系统

单店餐厅的扫码点菜 + 在线支付 + 后厨出单系统。顾客扫桌上小程序码点菜并微信支付，店员在管理端看桌况、接单响铃，后厨自动打印小票。

## 技术栈

| 层 | 选型 |
|---|---|
| 后端 | .NET 10 (ASP.NET Core Minimal API) + Dapper + MySqlConnector |
| 数据库 | MySQL 8.0 |
| 顾客端 | 微信小程序（原生） |
| 管理端 | 微信小程序（原生）+ Web 后台 |
| 实时推送 | 原生 WebSocket（不用 SignalR） |
| 支付 | 微信支付 APIv3（自行签名，不引第三方 SDK） |
| 部署 | Ubuntu 24.04 + Kestrel + systemd + Nginx |

## 目录

```
restaurant\
├─ docs\            设计文档（唯一真相，改代码前先看这里）
├─ miniapp\         顾客端小程序（微信开发者工具项目根，AppID 1）
├─ admin-miniapp\   管理端小程序（微信开发者工具项目根，AppID 2）
├─ server\          后端 .NET 解决方案
│  ├─ src\          Api / Core / Data / Integration 四个项目
│  ├─ tests\        单元测试
│  ├─ db\           DDL 与迁移脚本
│  ├─ certs\        商户私钥（已 gitignore）
│  └─ deploy\       systemd unit、Nginx 配置、备份脚本
└─ web-admin\       Web 后台
```

## 文档索引

| 文件 | 内容 |
|---|---|
| `docs/00-overview.md` | 范围、决策、技术栈、里程碑、风险 |
| `docs/01-pages.md` | 三端页面清单与交互 |
| `docs/02-data-model.md` | 14 张表 DDL、索引、.NET 类型映射 |
| `docs/03-api.md` | REST 接口契约、WebSocket 协议、支付流程 |
| `docs/04-deploy-linux.md` | Ubuntu 部署手册 |

## 本地开发

```bash
# 后端
cd server
dotnet build
dotnet run --project src/Restaurant.Api

# 数据库（首次）
mysql -u root -p < db/001-schema.sql
mysql -u root -p restaurant < db/002-seed.sql
```

小程序用微信开发者工具分别导入 `miniapp` 与 `admin-miniapp` 两个目录。本地联调时在「详情 → 本地设置」勾选 **不校验合法域名**，即可直连 `http://localhost:5000`。

## 约定

- 金额一律用**分**为单位的整数（`*Cents` / C# `long`），不允许浮点参与计算
- 订单挂**会话**（`table_sessions`）而非桌号，避免桌号复用导致串单
- `order_items` 是价格快照表，菜品改价不影响历史订单
- 所有时间以数据库 `DATETIME` 存本地时区（Asia/Shanghai），服务器时区必须配好
