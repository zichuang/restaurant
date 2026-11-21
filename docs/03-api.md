# 接口设计（HTTP API + WebSocket + 微信支付 v3）

> 版本 v0.4 · 2026-11-17 · 待评审
> v0.4 变更：承载方式由 IIS 改为 **Ubuntu 24.04 + Kestrel + systemd + Nginx**（应用只监听 `127.0.0.1:5000`）。**接口契约自 v0.2 起从未改变**。
> v0.3 变更：实现层由 Node.js 改为 **ASP.NET Core 10**，只替换实现要点。
> 本文档原名 `03-cloud-functions.md`，v0.3 起正名为 `03-api.md`，旧文件已删除。
> 路径写法约定：文档中统一写 `/orders/:id`，C# 中对应 `/orders/{id:long}`，语义相同。

---

## 一、通用约定

### 1.1 基础信息

```
Base URL    https://你的域名/api
协议        HTTPS（仅 443，不带端口）
报文        JSON，UTF-8
命名        API 层 camelCase，数据库层 snake_case，由 Repository 映射
时间        接口输出 ISO 8601 带偏移（如 2026-11-17T11:32:00+08:00）；数据库一律存 UTC
```

### 1.2 鉴权

```
Authorization: Bearer <JWT>
```

- JWT 载荷：`{ openid, shopId, role, iat, exp }`，有效期 7 天，HS256
- 三种角色策略：`Guest`（仅需合法用户）、`Staff`（waiter+）、`Web`（后台账号）
- **不信任任何客户端传入的 openid**，一律从 token 取
- C# 实现注意：`JwtBearerOptions.MapInboundClaims = false`，否则 `openid`、`role` 会被映射成 `ClaimTypes.*` 长 URI，读取时对不上

### 1.3 统一返回

```json
{ "code": "OK", "message": "", "data": {}, "traceId": "8f2c1a" }
```

| HTTP | 含义 |
|---|---|
| 200 | 业务成功（`code = OK`） |
| 400 | 参数错误 `PARAM_INVALID` |
| 401 | 未登录或 token 过期 `UNAUTHORIZED` |
| 403 | 无权限 `NO_PERMISSION` |
| 404 | 资源不存在 `NOT_FOUND` |
| 409 | 状态冲突 `ORDER_STATUS_INVALID`、`SESSION_CLOSED` |
| 429 | 限流 `RATE_LIMITED` |
| 500 | 服务异常 `SERVER_ERROR` |

业务异常统一抛 `BizException(code, message, httpStatus)`，由一个 `IExceptionHandler` 转成上面的信封；**不要在 endpoint 里到处写 try/catch**。

### 1.4 幂等

| 场景 | 幂等键 | 行为 |
|---|---|---|
| 提交订单 | `clientToken`（唯一索引） | 重复提交返回**已有订单**，不报错 |
| 开台 | `table_id + open`（生成列唯一索引） | 已存在直接返回该会话 |
| 支付回调 | `out_trade_no`（唯一索引） | 已成功直接应答成功 |
| 接单/出餐 | 目标状态（条件更新） | 已是目标状态返回成功 |
| 退款 | `outRefundNo` | 重复请求返回已有退款单 |

### 1.5 错误码

| code | 说明 |
|---|---|
| `PARAM_INVALID` | 参数缺失或格式错 |
| `UNAUTHORIZED` | token 缺失/过期 |
| `NOT_STAFF` / `NO_PERMISSION` | 不是员工 / 角色不足 |
| `TABLE_NOT_FOUND` / `TABLE_DISABLED` | 桌码无效 / 桌台停用 |
| `SESSION_CLOSED` / `SESSION_EXPIRED` | 会话已清台 / 已过期 |
| `SHOP_CLOSED` | 非营业时间 |
| `DISH_OFF_SHELF` / `DISH_SOLD_OUT` | 下架 / 沽清 |
| `PRICE_CHANGED` | 价格变动，附差异明细 |
| `AMOUNT_LIMIT` | 超过单笔上限 |
| `ORDER_STATUS_INVALID` | 状态不允许该操作 |
| `PAY_FAILED` / `REFUND_FAILED` | 支付/退款失败 |
| `RATE_LIMITED` | 触发限流 |
| `SERVER_ERROR` | 未预期异常（对外不暴露堆栈，详见 traceId 日志） |

### 1.6 时区与精度

- 数据库时间列一律存 **UTC**（`DATETIME(3)`），接口输出转 `+08:00`
- 金额一律 `long`（单位：分），序列化为 JSON number
- 所有 id 用 `BIGINT UNSIGNED` → C# `ulong`；对外转字符串或 number 需前后端统一（**本方案统一用 number，id 不超过 2^53 前都安全**）

---

## 二、登录与身份

### `POST /auth/login`

**入参**：`{ code }`（`wx.login` 得到的 code）

**服务端**：调 `https://api.weixin.qq.com/sns/jscode2session` 换 `openid`，查 `staff` 表。

**出参**

```json
{
  "token": "eyJhbGciOi...",
  "openid": "oXXXX",
  "staff": { "name": "王姐", "role": "waiter" }
}
```

`staff` 为 `null` 表示不是员工，管理端据此跳转「申请加入」。

**C# 要点**：`HttpClient` 用 `IHttpClientFactory` 命名客户端（不要 `new HttpClient()`）；微信返回是 `text/plain` 但内容是 JSON，且失败时也返回 200 + `errcode`，**必须判 `errcode`，不能只看状态码**。

### `POST /auth/apply`

**入参**：`{ name, role }` → 写 `staff_applications`（`pending`）

---

## 三、接口总表

### 3.1 顾客端

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/session/open` | 扫码开台，`{ scene }` |
| GET | `/menu` | 分类 + 菜品（带 `version`） |
| POST | `/cart/validate` | 提交前校验估清与价格 |
| POST | `/orders` | 创建订单（服务端算价） |
| POST | `/orders/:id/pay` | 生成微信支付参数 |
| GET | `/orders/:id` | 订单详情（含补偿查单） |
| GET | `/sessions/:id/orders` | 本桌订单列表 |
| POST | `/orders/:id/refund-apply` | 申请退款 |

### 3.2 管理端

| 方法 | 路径 | 权限 |
|---|---|---|
| GET | `/admin/tables` | waiter+ |
| GET | `/admin/sessions/:id` | waiter+ |
| POST | `/admin/orders/:id/accept` | waiter+ |
| POST | `/admin/orders/:id/serve` | waiter+ |
| POST | `/admin/sessions/:id/close` | waiter+ |
| PATCH | `/admin/dishes/:id/status` | manager+ |
| POST | `/admin/orders/:id/reprint` | waiter+ |
| POST | `/admin/orders/:id/refund` | manager+ |

### 3.3 Web 后台

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/web/login` | 账号密码登录 |
| GET/POST/PATCH/DELETE | `/web/categories` `/web/categories/:id` | 分类 |
| GET/POST/PATCH/DELETE | `/web/dishes` `/web/dishes/:id` | 菜品 |
| POST | `/web/dishes/import` | CSV 批量导入 |
| GET/POST/PATCH/DELETE | `/web/tables` `/web/tables/:id` | 桌台 |
| POST | `/web/tables/:id/qrcode` | 生成桌码 |
| GET | `/web/tables/qrcode/zip` | 批量打包下载 |
| GET | `/web/orders` | 订单查询（分页、筛选） |
| GET | `/web/orders/export` | 导出 Excel |
| GET/POST/PATCH | `/web/printers` | 打印机 |
| POST | `/web/printers/:id/test` | 测试打印 |
| GET/POST/PATCH | `/web/staff` | 员工与申请审批 |
| GET | `/web/stats/overview` | 经营概览（二期） |

### 3.4 微信支付回调

| 方法 | 路径 | 鉴权 |
|---|---|---|
| POST | `/pay/callback` | **无 JWT，验签代替** |
| POST | `/refund/callback` | 同上 |

> 回调接口必须在路由上跳过 JWT 鉴权与限流，且**关闭请求体大小限制**（回调报文虽小，但被限流挡掉会导致微信重试风暴）。

---

## 四、关键接口详述

### 4.1 `POST /session/open`

**入参**：`{ scene }` —— 小程序码 scene，需 `Uri.UnescapeDataString` 后校验 `^[A-Za-z0-9]{1,8}$`

**逻辑**

1. 按 `no` 查 `dining_tables`
2. 校验桌台存在、`status != disabled`、门店 `status = on`、当前在营业时段
3. 查该桌 `status='open'` 会话：存在且未过期 → 复用；已过期 → 关闭旧的再新建；不存在 → 新建（`expire_at = now + session_ttl_minutes`），同时 `dining_tables` 置 `occupied` 并写 `current_session_id`
4. **并发保护**：`table_sessions.open_flag` 唯一索引，插入冲突（`MySqlException.Number == 1062`）时回查已存在会话

**出参**

```json
{
  "shop":   { "id": 1, "name": "老张家常菜", "notice": "今日推荐：剁椒鱼头" },
  "table":  { "id": 8, "no": "A08", "zone": "大厅" },
  "session":{ "id": 120, "openAt": "2026-11-17T11:32:00+08:00", "expireAt": "..." },
  "unpaidOrder": { "id": 331, "totalCents": 8600 }
}
```

### 4.2 `POST /orders`

**入参**

```json
{
  "sessionId": 120,
  "clientToken": "uuid-v4",
  "remark": "不要香菜",
  "expectedTotalCents": 8600,
  "items": [{ "dishId": 12, "qty": 1, "specIndexes": [0], "addonIndexes": [], "remark": "微辣" }]
}
```

**服务端必做**

1. `clientToken` 已存在 → 直接返回该订单（幂等，不回错误）
2. 会话校验：`open` 且未过期，`shop_id` 与 token 一致
3. 逐个 `dishId` 查库：`status='on'` 且 `sold_out=0`
4. 按当前价重算，`expectedTotalCents` 不一致 → `PRICE_CHANGED` + 差异明细
5. 总额 ≤ `shops.order_limit_cents`
6. 事务内：取号 → 插 `orders` → 批量插 `order_items` → `table_sessions.order_count + 1`
7. 返回 `{ orderId, orderNo, totalCents, seq }`

**限流**：同一 openid 10 秒 1 次。用 `AddRateLimiter` 固定窗口策略，分区键取 JWT 的 `openid`。

**C# 要点**：`clientToken` 冲突同样靠唯一索引兜底（错误码 1062），不要先 `SELECT` 再 `INSERT`——两个请求同时进来会双双通过。

### 4.3 `POST /orders/:id/pay`

**逻辑**：校验订单属于当前 openid 且 `status='pending_pay'` → 生成 `out_trade_no`（`T` + 17 位时间戳 + 4 位随机）→ 写 `payments(created)` → 调微信支付 v3 JSAPI 下单。

**出参**

```json
{
  "payParams": {
    "timeStamp": "1763000000",
    "nonceStr": "...",
    "package": "prepay_id=wx...",
    "signType": "RSA",
    "paySign": "..."
  }
}
```

### 4.4 `POST /pay/callback`（幂等核心）

**处理顺序**

1. 验签：用微信支付平台证书验 `Wechatpay-Signature`，失败直接返回 401（微信会重试）
2. 用 APIv3 密钥 AES-256-GCM 解密 `resource`，得到 `out_trade_no`、`transaction_id`、`amount.total`
3. 事务：
   - `UPDATE payments SET status='success'... WHERE out_trade_no=? AND status='created'`，`affectedRows=0` → 已处理，直接成功应答
   - 校验金额与 `payments.amount_cents` 一致，不一致告警并人工介入
   - `UPDATE orders SET status='paid'... WHERE id=? AND status='pending_pay'`
   - `UPDATE table_sessions SET paid_cents = paid_cents + ?`
4. **事务提交后**：广播 WebSocket `order.created` → 写 `print_tasks`
5. 应答 `{"code":"SUCCESS","message":"成功"}`

> 推送与打印必须在提交之后。若放在事务内，一旦回滚就会出现「铃响了但库里没单」——这是最难排查的故障。

**补偿**：`GET /orders/:id` 发现 `pending_pay` 且超过 1 分钟，主动调微信查单接口，状态为成功则走与回调一致的落库逻辑（同样幂等），每单最多查 3 次。

### 4.5 `POST /admin/orders/:id/accept` / `serve`

```sql
UPDATE orders SET status='accepted', accepted_at=UTC_TIMESTAMP(3) WHERE id=? AND status='paid';
```

`affectedRows=0` → `409 ORDER_STATUS_INVALID`。成功后广播 `order.updated`（管理端据此停止响铃）。

### 4.6 `POST /admin/sessions/:id/close`

校验会话内不存在 `pending_pay` / `paid` / `accepted` 的订单，否则返回 409 并列出未完成单号。

事务：`table_sessions` → `closed`；`dining_tables` → `idle`、`current_session_id = NULL`。提交后广播 `session.closed`。

### 4.7 `POST /admin/orders/:id/refund` / `POST /web/orders/:id/refund`

**权限**：manager+。顾客端只能 `refund-apply`（写 `refunds` 待审，不改订单）。

1. 校验 `amount ≤ paid_cents - refunded_cents`
2. 调微信退款接口
3. 成功 → 事务：`refunds` → `success`；全额退则 `orders.status='refunded'`，部分退只累加 `refunded_cents` 并**保持原状态**（否则后厨会误以为整单取消）
4. 提交后广播 `order.updated`

### 4.8 `POST /web/tables/:id/qrcode`

1. 用 `access_token` 调 `POST https://api.weixin.qq.com/wxa/getwxacodeunlimit`，`scene = table.no`、`page = pages/entry/index`、`width = 430`、`check_path = true`
2. 返回的二进制写入 `uploads/qrcode/{shopId}/{no}.png`（站点目录下，**非源码目录**）
3. 更新 `dining_tables.qr_file_id`、`qr_generated_at`
4. **修改桌号时必须清空 `qr_file_id`** 并提示重新生成

> 微信这个接口成功时返回图片二进制（`Content-Type: image/jpeg`），失败时才返回 JSON。C# 里先看 `Content-Type` 再决定是解析错误还是写文件——只按状态码判断会把图片当 JSON 解析而抛异常。

---

## 五、WebSocket 实时推送

### 5.1 连接

```
wss://你的域名/ws?token=<JWT>
```

- 建连后 5 秒内必须发 `{"type":"sub"}`，否则服务端断开
- 服务端按 `shopId` 分组维护连接（`ConcurrentDictionary<long, ConcurrentDictionary<Guid, ClientConn>>`）
- 心跳：服务端每 30 秒发 ping 帧；60 秒未收到 pong 则 `Abort()`

### 5.2 下行事件

| type | 触发时机 | data |
|---|---|---|
| `order.created` | 支付成功 | `{ orderId, tableNo, amountCents, itemsCount, seq }` |
| `order.updated` | 接单/出餐/退款 | `{ orderId, tableNo, status }` |
| `session.closed` | 清台 | `{ sessionId, tableId }` |
| `dish.changed` | 沽清/上下架 | `{ dishId, soldOut }` |

### 5.3 上行事件

| type | 说明 |
|---|---|
| `sub` | 订阅本店事件（`shopId` 从 token 取，不接受客户端指定） |
| `ack` | 收到事件后的确认，便于排查丢推 |

### 5.4 客户端重连策略

```
第 1 次失败 → 1 秒后重连
第 2 次 → 2 秒
第 3 次 → 4 秒
之后固定 8 秒，最多 30 秒
连续 5 次失败 → 降级为 15 秒轮询 GET /admin/tables，并提示「实时提醒已降级」
```

### 5.5 服务端实现要点（ASP.NET Core）

```csharp
// Program.cs
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)   // 服务端主动 ping
});
app.Map("/ws", WsEndpoint.HandleAsync);
```

四条**必须**遵守的实现规则：

1. **发送必须串行化**。同一个 `WebSocket` 实例并发调 `SendAsync` 会抛 `InvalidOperationException: There is already one outstanding 'SendAsync' call`。每个连接持一个 `SemaphoreSlim(1,1)`，广播时逐连接 `await`，且**不要**串行等待全部连接（慢客户端会拖死广播）——用 `Task.WhenAll` + 单连接超时 2 秒，超时直接剔除。
2. **只在事务 commit 之后广播**。禁止在 `TransactionScope`/`MySqlTransaction` 内部推。
3. **广播器是进程内单例**，注册为 `Singleton` 的 `IOrderBroadcaster`。这决定了服务**只能单进程运行**：不要起多个 systemd 实例、不要多端口多进程、不要做负载均衡。
4. **接收循环与发送分离**。接收在 `while (socket.State == WebSocketState.Open)` 里 `ReceiveAsync`；发送走广播器。不要在接收循环里做业务查询，否则一个慢 SQL 会让该连接的心跳被误判超时。

**进程重启的处理**：`systemctl restart`、发布更新、崩溃拉起时连接都会被直接断开，这不是 bug，是机制。`ApplicationStopping` 里给客户端发一个 `{"type":"bye"}` 让前端立刻重连（而不是等 TCP 超时），能把恢复时间从 20 秒压到 1 秒内。

```csharp
app.Lifetime.ApplicationStopping.Register(() =>
    broadcaster.NotifyShutdown("server_recycling"));
```

---

## 六、微信支付 v3 对接

### 6.1 需要准备的凭证

| 项 | 说明 |
|---|---|
| `mchid` | 商户号 |
| 商户 API 证书 | `apiclient_cert.pem` + `apiclient_key.pem` |
| 证书序列号 | 在商户平台查看 |
| APIv3 密钥 | 32 字节，用于回调验签解密 |

私钥放 `/var/www/restaurant-api/secrets/`，属主 `root:restaurant`、权限 `750`（文件 `640`），**运行用户只能读、不能写**，`.gitignore` 排除，不随站点目录公开静态访问。

### 6.2 JSAPI 下单

```
POST https://api.mch.weixin.qq.com/v3/pay/transactions/jsapi
Authorization: WECHATPAY2-SHA256-RSA2048 mchid="...",nonce_str="...",signature="...",timestamp="...",serial_no="..."
```

请求体：

```json
{
  "appid": "...", "mchid": "...", "description": "老张家常菜-A08",
  "out_trade_no": "T17630000001234", "notify_url": "https://你的域名/api/pay/callback",
  "amount": { "total": 8600, "currency": "CNY" },
  "payer": { "openid": "oXXXX" }
}
```

签名串：`POST\n/v3/pay/transactions/jsapi\n{timestamp}\n{nonce}\n{body}\n`。

**C# 签名**（BCL 内置，不需要第三方库）：

```csharp
using var rsa = RSA.Create();
rsa.ImportFromPem(File.ReadAllText(keyPath));          // PKCS#8 PEM
var sig = rsa.SignData(
    Encoding.UTF8.GetBytes(signStr),
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);
var signature = Convert.ToBase64String(sig);
```

`prepay_id` 拿到后，前端 `paySign` 的签名串：

```
{appId}\n{timeStamp}\n{nonceStr}\nprepay_id={prepay_id}\n
```

同样用**商户私钥**签（不是 APIv3 密钥，两者常被搞混）。

### 6.3 回调验签

- 取 `Wechatpay-Signature` / `Wechatpay-Timestamp` / `Wechatpay-Nonce` / `Wechatpay-Serial`
- 用对应**平台证书**验签，验签串：`{timestamp}\n{nonce}\n{body}\n`
- 平台证书需定期从 `GET /v3/certificates` 拉取更新（建议每日一次后台任务）
- 解密 `resource`：AES-256-GCM，密钥为 APIv3 密钥，`nonce` 与 `associated_data` 来自报文
- 应答 HTTP 200 + `{"code":"SUCCESS","message":"成功"}`，否则微信会按策略重试

**C# 三个坑**：

```csharp
// 1) 验签：必须先重算签名再常量时间比较
var ok = rsa.VerifyData(Encoding.UTF8.GetBytes(msg), sigBytes,
           HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

// 2) .NET 8+ 的 AesGcm 构造函数必须显式给 tag 长度，旧的单参重载已过时
using var aes = new AesGcm(key, tagSizeInBytes: 16);
aes.Decrypt(nonce, ciphertext, tag, plaintext);

// 3) 回调 body 必须读原始字节/字符串，不能用 [FromBody] 自动反序列化
//    验签对象是原始报文，反序列化后再序列化会因空格/字段顺序不同而验签失败
```

第 3 条最容易踩：`[FromBody]` 或 `ReadFromJsonAsync` 之后再 `JsonSerializer.Serialize` 去验签，几乎必然失败。正确做法是端点里 `app.Use(async ctx => { ctx.Request.EnableBuffering(); ... })` 取原始串，验签通过后再手工反序列化。

---

## 七、access_token 管理

调小程序码、订阅消息等接口都需要 `access_token`（7200 秒有效）。

| 项 | 做法 |
|---|---|
| 获取 | `GET https://api.weixin.qq.com/cgi-bin/token?grant_type=client_credential&appid=&secret=` |
| 缓存 | `IMemoryCache` 单例，`AbsoluteExpirationRelativeToNow = 7000 秒` |
| 刷新策略 | **提前 5 分钟**刷新；调用返回 `40001/42001` 时强制刷新一次并重试 |
| 并发 | `SemaphoreSlim(1,1)` + 双重检查，避免并发刷新导致后拿到的 token 把前一个顶掉 |
| 进程重启 | **重启会清空 `IMemoryCache`**，重启后第一次调用重新获取即可——所以绝不能把 token 落到配置文件或数据库当「持久值」用 |
| 多实例 | 单站点单进程无此问题；将来多实例改为存 MySQL `sys_config` 表 + 行锁 |

---

## 八、后台任务（BackgroundService）

| 任务 | 频率 | 职责 |
|---|---|---|
| 关超时单 | 每 5 分钟 | `pending_pay` 且超过 `auto_close_minutes` → 先查微信真实状态，未付则置 `cancelled` |
| 关闭过期会话 | 每 10 分钟 | `expire_at < now` 且 `open` → 若无非终态订单则关闭，桌台置空闲 |
| 打印分发 | 每 10 秒 | 取 `pending` 任务（按 `created_at` 升序，最多 20 条）→ 调打印机 HTTP API → 成功 `done`，失败 `retry_count+1`，≥3 次置 `failed` 并告警 |
| 清理打印任务 | 每天 03:10 | 删除 90 天前的 `print_tasks` |
| 平台证书更新 | 每天 04:00 | 拉取微信支付平台证书 |
| 数据库备份 | 每天 03:00 | 交给 **cron** 跑 `mysqldump`，不放进应用（见 `04-deploy-linux.md` 第八节） |

**实现约定**：

```csharp
public sealed class OrderCloseJob : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(ct))   // ct 取消即退出，不需要手写 while(true)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "关单任务失败，下一轮重试"); }
        }
    }
}
```

三条纪律：

1. **每个任务必须幂等可重入**。`systemctl restart` 与发布会在任意时刻 `Cancel`，任务可能执行到一半，下一轮要能安全重跑。
2. **单个任务异常不能吞掉循环**。try/catch 包在单轮逻辑外，否则一次数据库抖动就永久停摆。
3. **`HostOptions.ShutdownTimeout` 设为 30 秒**，systemd 侧 `TimeoutStopSec=30` 与之对齐（见 `04-deploy-linux.md` 第五节）。超时后 systemd 会发 `SIGKILL`，所以每个任务必须能在 30 秒内响应取消。

> 打印分发是唯一不能容忍中断的任务（顾客付了钱小票没出来）。所以它除了后台轮询，**支付回调里同时写 `print_tasks` 并立即尝试打印一次**，后台任务只是兜底重试。

---

## 九、ASP.NET Core 工程结构与中间件顺序

```
Restaurant.Api/
├─ Program.cs                 组装：DI、中间件、Endpoint 映射
├─ Endpoints/                 按域分文件：Auth、Session、Menu、Order、Admin、Web、Pay
├─ Middleware/
│  ├─ ExceptionHandler.cs     BizException → 统一信封
│  ├─ RequestLogging.cs       traceId + 耗时
│  └─ JwtPolicies.cs          Guest / Staff / Web
├─ Ws/
│  ├─ WsEndpoint.cs           握手、鉴权、sub 超时
│  ├─ ConnectionManager.cs    分组与广播（Singleton）
│  └─ ClientConn.cs           SendAsync 串行化 + lastActiveAt
└─ appsettings.json           Production 版放连接串与开关，密钥不进仓库
```

**中间件顺序（顺序错会出各种怪问题）**：

```csharp
app.UseForwardedHeaders();          // 1. 必须最前：反代后还原 https 与真实 IP
app.UseExceptionHandler();          // 2. 兜住一切
app.UseSerilogRequestLogging();     // 3. 记 traceId 与耗时
app.UseWebSockets(...);             // 4. 必须在路由之前
app.UseStaticFiles();               // 5. uploads 静态资源
app.UseAuthentication();            // 6. 先认证
app.UseAuthorization();             // 7. 再授权
app.UseRateLimiter();               // 8. 限流在授权之后（要能拿到 openid）
app.MapXxxEndpoints();              // 9. 最后映射
```

> `UseWebSockets` 放到 `UseRouting` 之后、`MapXxx` 之前也能工作，但**绝对不能放在 `UseAuthorization` 之后**——否则 `/ws` 会被 401 提前短路，握手永远失败，而且客户端只会看到「连接关闭」这种没有信息量的错误。

---

## 十、Nginx 反代与部署要点（详见 `04-deploy-linux.md`）

小程序后台必须配置（「开发管理 → 服务器域名」）：

- request 合法域名：`https://你的域名`
- socket 合法域名：`wss://你的域名`

域名须已备案、证书由受信任 CA 签发、不能带端口。任何一项不符，`wx.request` 会直接失败且报错信息不明确。

反向代理侧四条与本接口文档强相关的配置：

| 配置 | 值 | 原因 |
|---|---|---|
| `/ws` 的 `proxy_set_header Upgrade` / `Connection` | 必须设置 | 缺任一则握手 400/426，且服务端没有日志 |
| `/ws` 的 `proxy_read_timeout` | `3600s` | 默认 60 秒会切断空闲长连接，表现为「每 60 秒掉一次」 |
| `/ws` 的 `proxy_buffering` | `off` | 开缓冲会让推送延迟数秒，响铃变慢 |
| `client_max_body_size` | `10m` | 菜品图上传；默认 1MB 直接 413 |

两条与应用侧配合的：

- **`ForwardedHeaders` 必须启用**，且信任 `127.0.0.1`。Nginx 传 `X-Forwarded-Proto`，应用才知道真实协议；否则 `Request.Scheme` 恒为 `http`，拼出的 `notify_url` 与图片 URL 全部变成 `http://`。
- Kestrel `MaxRequestBodySize`：`10 * 1024 * 1024`，与 Nginx 的 `client_max_body_size` 对齐，任一边偏小都会拦掉上传。

应用只监听 `127.0.0.1:5000`，**不要绑 `0.0.0.0`**——那等于绕过 Nginx 直连 Kestrel，TLS 和转发头都会白配。

---

## 十一、安全清单（上线前逐条核对）

- [ ] 金额全部由服务端按 `dish_id` 重算，客户端价格字段一律忽略
- [ ] 所有带 `shopId` 的接口校验资源归属，防跨店越权（**最容易漏的一条**）
- [ ] 支付回调验签 + 金额比对 + 唯一索引幂等
- [ ] 回调验签用**原始报文**，禁止先反序列化再重新序列化
- [ ] 订单状态全部条件更新，禁止先查再无条件改
- [ ] `createOrder` 按 openid 限流 10 秒 1 次
- [ ] MySQL 只监听 `127.0.0.1`，禁用 root 远程登录，应用用独立低权限账号
- [ ] JWT 密钥、APIv3 密钥、商户私钥全部走环境变量或受限目录，不写进代码、不入 git
- [ ] 上传文件校验类型白名单与大小上限，重命名后落盘，禁止保留原始文件名
- [ ] 打印机 `secret` AES 加密存储，不下发到任何客户端
- [ ] 日志不打印完整 token、私钥、回调原始报文中的敏感字段
- [ ] 防火墙只开 80/443（远程管理端口改非默认），数据库端口不对外
- [ ] Nginx 关闭 `server_tokens` 与目录浏览，应用不返回 `Server` 头与版本号
- [ ] `/api/ping` 之类探活接口不返回版本号与内部路径
- [ ] 小程序后台配置隐私保护指引（相册、手机号等）
