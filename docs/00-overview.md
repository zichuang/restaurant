# 餐厅扫码点菜系统 · 总体方案

> 版本 v0.4 · 2026-11-17 · 待评审
> v0.4 变更：承载方式由 **Windows Server + IIS** 改为 **Ubuntu 24.04 LTS + Kestrel + systemd + Nginx**。后端语言仍为 .NET 10，接口契约与 DDL 不变。
> v0.3 变更：后端确定为 **.NET 10（ASP.NET Core）+ MySQL 8.0**，放弃 Node.js。
> v0.2 变更：后端由「微信云开发」改为自建。
> 本文档为方案定稿前的评审稿，代码以本文档为准。评审通过后再进入工程搭建。

---

## 一、目标与范围

### 一期要做

顾客扫桌上二维码 → 浏览菜单 → 加购物车 → 提交订单 → 微信支付 → 管理端响铃提醒 → 后厨打印出餐，同时支持店长管理桌台与菜品。

### 一期明确不做

| 不做 | 原因 | 后续 |
|---|---|---|
| 支付宝支付 | 微信小程序内无接口唤起支付宝 | 二期做聚合码 + 支付宝小程序/H5 |
| 会员储值、优惠券、满减 | 独立营销模块，会拖长一期 | 二期 |
| 外卖 / 自提 | 履约模型完全不同 | 二期 |
| 多人同桌共享购物车 | 需实时协同与冲突合并 | 二期 |
| 后厨 KDS 大屏 | 云打印机成本更低、更稳 | 二期 |
| 连锁多门店 | 表结构已预留 shop_id，但只做单店 | 二期 |
| 多服务器横向扩展 | 单店单机足够；广播器是进程内单例，不做跨实例 | 二期（上 Redis pub/sub） |

### 非功能要求

- 顾客从扫码到看到菜单 ≤ 2 秒（菜单接口带内存缓存）
- 下单到管理端响铃 ≤ 2 秒（WebSocket 推送）
- 支付回调到订单变「已支付」≤ 3 秒
- 全部金额以「分」为整数存储与计算，禁止浮点
- 数据库每日全量备份 + binlog，保留 30 天
- 进程崩溃由 systemd 自动拉起；**WebSocket 断线后客户端 8 秒内重连**（重启、发布必然断连，靠客户端重连兜底，不追求服务端不断线）

---

## 二、角色与终端

| 角色 | 终端 | 主要动作 | 是否需要登录 |
|---|---|---|---|
| 顾客 | 顾客端小程序 | 扫码、点菜、支付、看订单进度 | 不需要（openid 静默识别） |
| 服务员 / 店长 | 管理端小程序 | 看桌况、接单、出餐、沽清、清台 | 需要（员工绑定 + 角色校验） |
| 后厨 | 云打印机 | 自动打印小票 | 不需要（设备绑定） |
| 老板 | Web 后台 | 菜品维护、桌台维护、订单查询、退款、报表 | 需要（账号密码 + 微信扫码） |

---

## 三、已确认决策

| # | 决策项 | 结论 | 影响 |
|---|---|---|---|
| 1 | 经营主体 | 个体工商户 | 可申请微信支付商户号；餐饮类目需《食品经营许可证》 |
| 2 | 结账模式 | **点完即付，加菜单独结算** | 一次开台挂多个订单，每单独立支付与退款 |
| 3 | 管理端形态 | **管理端小程序 + Web 后台** | 一套后端撑三个前端 |
| 4 | 后端语言与框架 | **.NET 10（ASP.NET Core Minimal API）** | 维护者熟悉 C#，长期可维护性最高；无 npm 依赖 |
| 5 | 数据访问 | **Dapper + MySqlConnector**（不用 EF Core） | 绕开 EF Core 10 的 MySQL Provider 断档（见 4.6） |
| 6 | 承载方式 | **Ubuntu 24.04 LTS + Kestrel + systemd + Nginx** | 应用只监听 `127.0.0.1:5000`，Nginx 终结 TLS 并反代 |
| 7 | 数据库 | **MySQL 8.0**（开发机 8.0.46） | 本机开发与生产同一大版本 |
| 8 | 实时提醒 | **原生 WebSocket（wss）** | 不用 SignalR：小程序端是裸 WebSocket，协议自定更省事 |

---

## 四、硬约束与技术边界

### 4.1 微信小程序内不能唤起支付宝

小程序运行环境只开放 `wx.requestPayment`（微信支付）。没有 URL Scheme、没有 H5 跳转、没有 SDK 能唤起支付宝 App。「小程序里同时支持微信和支付宝」在单个微信小程序内无法实现。

二期若必须收支付宝，只能做**聚合码**：微信扫进小程序，支付宝扫进支付宝小程序或 H5 收银台。两套前端，一个后端。

### 4.2 管理端退到后台就收不到推送

小程序没有常驻后台能力。WebSocket 在小程序切后台后会被系统挂起，连接随之断开。

| 通道 | 实时性 | 可靠性 | 定位 |
|---|---|---|---|
| WebSocket（页面在前台） | 1 秒内 | 高 | **主通道** |
| 订阅消息 | 秒级 | 低（一次性授权） | 补充 |
| 服务号模板消息 / 企业微信机器人 | 秒级 | 高 | **兜底**，由服务端触发 |

结论不变：店里必须有一台常开设备跑管理端页面。这是机制决定的，不是产品保守。

### 4.3 餐饮类目需要《食品经营许可证》

小程序类目报「餐饮服务」时须上传《食品经营许可证》，营业执照不能替代。没有的话备案和支付签约能过，但提审会被驳回。

### 4.4 桌码必须用小程序码

- 用 `wxacode.getUnlimited` 生成，桌号写在 `scene`（**限 32 个可见字符**，桌号用 `A08` 这类短编号）
- 普通链接二维码需域名校验，且扫码后经 H5 中转，多一步流失，不采用
- 桌码不能当作「人在店里」的凭证，防护靠：会话有效期（默认 180 分钟）、单笔金额上限、强制清台

### 4.5 Linux 承载带来的四条硬约束

这四条是 Windows/IIS 时代不存在（或形态完全不同）的，不写清楚上线必踩：

| # | 约束 | 后果 | 对策 |
|---|---|---|---|
| 1 | **Linux 默认 UTC 时区** | `DateTime.Now` 写库比北京时间早 8 小时；白天测不出来，晚上对账才发现 | `timedatectl set-timezone Asia/Shanghai` + systemd 里 `Environment=TZ=Asia/Shanghai` 双保险 |
| 2 | **反向代理后 `Request.Scheme` 变成 `http`** | 服务端拼出的绝对 URL（支付 `notify_url`、图片地址）变成 `http://`，微信侧校验失败 | 启用 `ForwardedHeaders` 中间件，且 Nginx 必须传 `X-Forwarded-Proto` |
| 3 | **Nginx 反代 WebSocket 有三个必配项** | 缺 `Upgrade` 头 → 握手 400/426；`proxy_read_timeout` 用默认 60s → 连接每 60 秒断一次；开缓冲 → 消息延迟数秒 | `/ws` 单独一个 `location`，见 `04-deploy-linux.md` 6.1 |
| 4 | **广播器是进程内存态，只能单进程** | 起两份进程会出现「A 收到单、B 拿着连接」，推送大量丢失 | 单 systemd 实例，**不要**改 `ASPNETCORE_URLS` 多端口起多份，不要上负载均衡 |

另外两条与部署强相关：

- **应用只绑 `127.0.0.1:5000`**，绑 `0.0.0.0` 等于绕过 Nginx 直连 Kestrel，TLS 白配、`X-Forwarded-*` 也失效
- **`libicu` 必须装**。缺失时应用启动直接抛 `Couldn't find a valid ICU package`；用 `InvariantGlobalization=true` 能绕过，但中文排序会退化，不要这么做

> 与 IIS 方案相比，以下问题在本方案中**不存在**：需要单独安装 WebSocket 功能、应用池空闲回收、重叠回收导致进程双跑、`IIS AppPool\` 目录授权、Hosting Bundle 与 Runtime 的安装歧义。

### 4.6 为什么不用 EF Core

`.NET 10` 对应的 EF Core 10 目前**没有可用的 Pomelo MySQL Provider**：`Pomelo.EntityFrameworkCore.MySql` 最新稳定版仍是 9.0.0，依赖范围写死 `Microsoft.EntityFrameworkCore.Relational [9.0.0, 9.0.999]`，没有 EF Core 10 的构建。强行引用会出现两种失败：restore 直接报 `NU1107`，或者只给 `NU1608` 警告、编译通过、**运行时在 `UseMySql()` 抛 `MissingMethodException`**——后者极难排查。

可选替代是 Oracle 的 `MySql.EntityFrameworkCore`（GPL 许可 + 换驱动）或 `Microting` fork。但本项目的 DDL 是手写的，且用了生成列 + 唯一索引这种 ORM migration 表达不好的写法，**Dapper 反而更直接**：SQL 可控、无隐藏 N+1、无版本耦合。故定 Dapper。

代价要说清楚：**没有 migration，表结构变更要自己写 `ALTER` 脚本**，并纳入 `server/db/` 目录按序号管理。

### 4.7 金额一律用整数「分」

C# 有 `decimal`，但**不要**因此把库里的 `*_cents` 改成 `DECIMAL(10,2)`。前端 JS 是双精度浮点，接口层必须统一传整数分，否则迟早出现 8600 变成 8599.999 的对账事故。C# 侧用 `long`。

---

## 五、技术栈

| 层 | 选型 |
|---|---|
| 顾客端 | 原生微信小程序（WXML/WXSS/JS），不引框架，包体积最小 |
| 管理端 | 原生微信小程序 |
| Web 后台 | Vue 3 + Vite + Element Plus（构建后作为静态站点，由 Nginx 托管） |
| 服务端 | **.NET 10 / ASP.NET Core Minimal API** |
| 数据访问 | **Dapper + MySqlConnector**（连接池由 MySqlConnector 内建） |
| 鉴权 | `Microsoft.AspNetCore.Authentication.JwtBearer`（HS256，密钥走环境变量） |
| 实时推送 | ASP.NET Core 原生 WebSocket（`app.UseWebSockets()`） |
| 后台任务 | `BackgroundService`（超时关单、打印分发、平台证书更新） |
| 日志 | Serilog + 文件滚动（按天，保留 30 天）+ journald |
| 数据库 | MySQL 8.0（utf8mb4 / utf8mb4_0900_ai_ci，InnoDB） |
| 操作系统 | **Ubuntu 24.04 LTS** |
| 应用托管 | **Kestrel（`127.0.0.1:5000`）+ systemd**（`Restart=always`） |
| 反向代理 | **Nginx**（TLS 终结、WebSocket 升级、静态站点） |
| 证书 | Let's Encrypt + certbot（systemd timer 自动续期） |
| 支付 | 微信支付 v3 API（JSAPI），C# 侧自行签名与验签 |
| 文件存储 | `/var/www/restaurant-api/uploads`，由应用 `UseStaticFiles` 输出；量大了再换对象存储 |
| 定时任务 | `BackgroundService` 为主；数据库备份交给 cron |

> 关于微信支付 SDK：官方 SDK 只覆盖 Java/PHP/Go/Node，C# 侧可选社区库 `SKIT.FlurlHttpClient.Wechat.TenpayV3` 或 `Senparc.Weixin.TenPayV3`。本项目只在 3 个调用点（下单、查单、退款）用到，**建议手写签名与验签**（约 200 行，`RSA` + `AesGcm` 都是 BCL 内置），少一个第三方依赖。

---

## 六、工程结构

```
E:\WX\MiniProGrame\
├─ P1\                        现有「小程序运行原理」演示工程（保留，与本项目无关）
└─ restaurant\
   ├─ docs\                   本套设计文档
   ├─ miniapp\                顾客端小程序（开发者工具项目根）
   ├─ admin-miniapp\          管理端小程序（第二个项目根）
   ├─ server\                 后端解决方案
   │  ├─ Restaurant.sln
   │  ├─ src\
   │  │  ├─ Restaurant.Api\           Web 入口：Program.cs、Endpoints、Middleware
   │  │  ├─ Restaurant.Core\          领域模型、算价、状态机、接口定义
   │  │  ├─ Restaurant.Data\          Dapper 仓储、SQL、事务封装
   │  │  └─ Restaurant.Integration\   微信登录、微信支付 v3、access_token、打印机
   │  ├─ tests\Restaurant.Tests\      算价与幂等的单元测试
   │  ├─ db\                          DDL 与 ALTER 迁移脚本（唯一真相）
   │  ├─ certs\                       商户私钥（.gitignore 排除）
   │  └─ deploy\                      部署制品
   │     ├─ restaurant-api.service    systemd unit
   │     ├─ nginx-restaurant-api.conf Nginx 站点配置
   │     └─ backup-restaurant.sh      备份脚本
   └─ web-admin\              Web 后台（构建后由 Nginx 托管静态文件）
```

服务器上的部署布局（与源码目录完全分开）：

```
/var/www/restaurant-api/          publish 产物（属主 restaurant）
/var/www/restaurant-api/uploads/  菜品图与桌码（属主 restaurant，755）
/var/www/restaurant-api/secrets/  商户私钥（root:restaurant，750，只读）
/var/log/restaurant/              Serilog 滚动日志
/var/backups/restaurant/          每日备份（保留 30 天）
/etc/systemd/system/restaurant-api.service
/etc/nginx/sites-available/restaurant-api
```

---

## 七、里程碑与验收

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **M0** | 资质 + 服务器环境 | 营业执照、食品经营许可证、小程序注册认证、微信支付商户号、两个 AppID；服务器装好 Ubuntu 24.04 + MySQL 8.0 + .NET 10 运行时 + Nginx，时区为 Asia/Shanghai，建库建表，`GET /api/ping` 跑通 |
| **M1** | 骨架 + 菜单 | 扫码进菜单页，分类与菜品正确渲染；图片能通过 Nginx 正常加载 |
| **M2** | 购物车 + 下单 | 能选规格、加备注、提交；服务端算价与前端一致；订单落库 `pending_pay` |
| **M3** | 支付 | 支付成功回调置 `paid`；重复回调不重复入账；超时未付自动关单 |
| **M4** | 管理端桌况 + 提醒 | 桌况网格正确；新单 2 秒内响铃；接单/出餐状态流转正确；**手动 `systemctl restart` 后 8 秒内自动重连** |
| **M5** | 桌台 + 菜品管理 | 菜品增删改、批量导入、上下架、沽清；桌台增删改并批量生成桌码 |
| **M6** | 后厨打印 | 支付成功自动打印；失败重试 3 次并告警 |
| **M7** | 上线 | ICP 备案完成、服务器域名白名单配置、隐私协议、提审通过、正式发布 |

M0 里「时区」和「`/ws` 的 101 握手」是两个必查项——时区错了会污染全部订单数据，握手不通则 M4 做完也测不出效果。

---

## 八、自建意味着运维责任在自己身上

| 事项 | 具体内容 |
|---|---|
| HTTPS 证书续期 | 证书到期当天小程序所有请求全部失败，且报错含糊。用 certbot + systemd timer 自动续期，**上线前必须跑一次 `--dry-run`** |
| 数据库备份 | 每日 `mysqldump --single-transaction` + 上传目录打包，保留 30 天；**必须做一次恢复演练** |
| 进程守护 | systemd `Restart=always` 自动拉起。**不要**为了"高可用"起多份进程——广播器是进程内存态，多份会丢推送 |
| 时区 | 系统时区 + systemd `TZ` 双重设置。运维换机器、重建系统时必须复查 |
| 磁盘监控 | journald、MySQL binlog、Serilog 日志、上传图片都会撑爆磁盘，需设上限并定时清理 |
| 安全 | MySQL 只监听 `127.0.0.1`、应用只监听 `127.0.0.1:5000`、ufw 只开 22/80/443、禁用 root 密码登录、接口限流 |
| access_token | 微信接口令牌 2 小时过期。进程重启会清空内存缓存，需能自动重新获取（不要把 token 写死到配置文件） |
| 系统更新 | 内核更新需重启，安排在非营业时段；安全补丁定期打 |

---

## 九、风险登记

| # | 风险 | 影响 | 应对 |
|---|---|---|---|
| R1 | 《食品经营许可证》未办，提审被拒 | 无法上线 | M0 同步办理，不阻塞开发 |
| R2 | 管理端设备息屏导致漏单 | 顾客下单无人接 | 常开设备 + `setKeepScreenOn`；未接单超时升级；服务号兜底 |
| R3 | 加菜单要再付一次，顾客困惑 | 投诉 | 加菜单页面固定提示；二期做合并支付 |
| R4 | 桌码被拍照远程下单 | 虚假订单、后厨空转 | 会话有效期 + 金额上限 + 清台 + 人工确认 |
| R5 | 支付回调丢失或重复 | 状态错乱、重复出餐 | 回调幂等（唯一索引 + 条件更新）+ 主动查单补偿 |
| R6 | 价格被前端篡改 | 资损 | 金额一律服务端按 dishId 重算 |
| R7 | **进程崩溃 / 发布重启** | WebSocket 断连、后台任务中断、access_token 缓存失效 | systemd 自动拉起 + 客户端退避重连 + 任务「幂等可重入」+ M4 专门验证 |
| R8 | **Nginx 漏配 WebSocket 反代** | 握手 400/426，提醒功能整体失效，服务端无日志 | 用 `curl` 验证返回 101 作为 M0 验收项 |
| R9 | **uploads 目录权限不对** | 菜品图上传 500 或静态访问 404 | 属主必须为 `restaurant`，用测试接口验证一次 |
| R10 | **服务器时区没设** | 订单时间整体偏 8 小时，对账与统计全错，且**事后难修正** | 时区双保险 + 验收时写一条测试订单核对 |
| R11 | **HTTPS 证书过期** | 小程序请求全部失败 | certbot 自动续期 + `--dry-run` 验证 + 到期提醒 |
| R12 | **数据库未备份 / 未演练** | 订单数据不可恢复 | 每日全量 + binlog + 异地 + 恢复演练 |
| R13 | 服务器单点故障 / 磁盘写满 | 全线停摆 | 磁盘告警；journald 与 binlog 设上限；备份可异地恢复 |
| R14 | 备案域名被回收或未通过年审 | 请求被拦 | 域名到期提醒；小程序后台域名白名单同步维护 |

---

## 十、成本估算

| 项目 | 估算 | 备注 |
|---|---|---|
| 小程序认证费 | 约 300 元/次 | 两个小程序各一次 |
| Linux 服务器 | 已有，边际成本 0 | 单店流量极小；Ubuntu 无授权费 |
| 域名与证书 | 已有 | Let's Encrypt 免费，certbot 自动续期 |
| MySQL | 0 | Ubuntu 官方仓库安装 |
| .NET 运行时 | 0 | 官方 apt 源免费 |
| 微信支付费率 | 约 0.6% | 餐饮类目，签约时确认 |
| 云打印机 | 300~800 元/台 | 后厨一台 |
| 常开平板 | 复用旧设备 | 建议 10 寸以上 |

固定成本接近于零，主要支出是支付费率与一次性硬件。

---

## 十一、文档索引

| 文档 | 内容 | 状态 |
|---|---|---|
| `01-pages.md` | 三端页面清单、路由、交互与依赖接口 | 不变（接口名一致，与后端语言无关） |
| `02-data-model.md` | MySQL 表结构、索引、事务、幂等、**.NET 类型映射** | v0.3（未受承载方式变更影响） |
| `03-api.md` | HTTP 接口 + WebSocket 协议 + 微信支付 v3 + **ASP.NET Core 实现要点** | v0.4 |
| `04-deploy-linux.md` | **Ubuntu 24.04 + systemd + Nginx 部署与上线**、参数说明、错误对照表 | v0.4（原 `04-deploy-iis.md` 已废弃删除） |

---

## 十二、待评审确认事项

1. 菜单规模与桌台数量（决定是否必须做批量导入、桌况页用网格还是列表）
2. 是否需要「服务员代客点菜」
3. 是否开发票（要的话订单表加税号与抬头字段）
4. 非营业时间是否硬性禁止开台
5. **Ubuntu 24.04 服务器是否已就绪**（全新装还是有存量服务）
6. **服务器上是否已有 Nginx 和其他站点**——决定 80/443 是否会冲突、是否需要改用独立 `server_name`
7. 是否允许开发阶段直接连生产库调试（**建议不连**，本地 MySQL 8.0.46 已就绪，够用）
