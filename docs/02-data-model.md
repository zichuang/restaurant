# 数据模型设计（MySQL 8.0）

> 版本 v0.3 · 2026-11-17 · 待评审
> v0.3 变更：后端改为 **.NET 10 + Dapper**，DDL 本身不变，新增第九章「.NET 侧类型映射与数据访问约定」，5.4 连接池改为 MySqlConnector 参数。
> v0.2 变更：由云开发文档型数据库改为 **MySQL 8.0 关系型**。
> 所有表默认只有后端能访问，小程序与 Web 后台无任何数据库直连权限。

---

## 一、命名与约定

| 约定 | 说明 |
|---|---|
| 字符集 | `utf8mb4` / `utf8mb4_0900_ai_ci`，全表统一 |
| 引擎 | InnoDB |
| 金额字段 | 一律以 `_cents` 结尾，`BIGINT UNSIGNED`，**整数，单位：分** |
| 时间字段 | `DATETIME(3)`，服务器与数据库时区统一 `Asia/Shanghai` |
| 状态字段 | `VARCHAR(20)` 存英文小写下划线枚举，不用 `ENUM`（改枚举要 DDL，代价高） |
| 主键 | `BIGINT UNSIGNED AUTO_INCREMENT`，业务编号另设唯一索引 |
| 软删除 | 关键业务表不物理删除，用 `status = 'deleted'` |
| 快照 | 订单表冗余 `table_no`、明细表冗余 `dish_name` / `unit_price_cents`，**必须冗余**：菜品改价改名不能影响历史订单 |
| 索引命名 | 唯一 `uk_*`，普通 `idx_*` |

---

## 二、状态枚举

### `orders.status`

| 值 | 含义 | 允许的下一步 |
|---|---|---|
| `pending_pay` | 待支付 | `paid`、`cancelled` |
| `paid` | 已支付，待接单 | `accepted`、`refunding` |
| `accepted` | 已接单，制作中 | `served` |
| `served` | 已出餐 | `completed` |
| `completed` | 已完成 | — |
| `cancelled` | 已取消（超时未付） | — |
| `refunding` | 退款中 | `refunded`、`paid`（失败回滚） |
| `refunded` | 已退款 | — |

### 其他

| 字段 | 取值 |
|---|---|
| `dining_tables.status` | `idle` / `occupied` / `disabled` |
| `table_sessions.status` | `open` / `closed` |
| `payments.status` | `created` / `success` / `closed` / `refunded` |
| `print_tasks.status` | `pending` / `printing` / `done` / `failed` |
| `staff.role` | `owner` / `manager` / `waiter` / `kitchen` |
| `dishes.status` | `on` / `off` / `deleted` |

---

## 三、表清单

| # | 表 | 说明 |
|---|---|---|
| 1 | `shops` | 门店与营业参数 |
| 2 | `dining_tables` | 桌台 |
| 3 | `table_sessions` | 桌台会话（一次开台） |
| 4 | `categories` | 菜品分类 |
| 5 | `dishes` | 菜品（规格/加料存 JSON） |
| 6 | `orders` | 订单 |
| 7 | `order_items` | 订单明细（价格快照） |
| 8 | `payments` | 支付流水 |
| 9 | `refunds` | 退款单 |
| 10 | `staff` | 员工 |
| 11 | `staff_applications` | 加入申请 |
| 12 | `printers` | 打印机 |
| 13 | `print_tasks` | 打印任务 |
| 14 | `order_seq` | 单号计数器 |

> 相比云开发版本少了 `order_feed`：自建用 WebSocket 直接推，不需要一个公开可读的广播集合。

---

## 四、DDL

### 4.1 `shops`

```sql
CREATE TABLE shops (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  name               VARCHAR(64)  NOT NULL,
  logo_url           VARCHAR(255) NOT NULL DEFAULT '',
  address            VARCHAR(255) NOT NULL DEFAULT '',
  phone              VARCHAR(32)  NOT NULL DEFAULT '',
  notice             VARCHAR(255) NOT NULL DEFAULT '',
  biz_hours          JSON         NULL COMMENT '如 [{"start":"10:00","end":"21:30"}]',
  session_ttl_minutes INT UNSIGNED NOT NULL DEFAULT 180,
  auto_close_minutes  INT UNSIGNED NOT NULL DEFAULT 15,
  order_limit_cents   BIGINT UNSIGNED NOT NULL DEFAULT 200000,
  status             VARCHAR(20)  NOT NULL DEFAULT 'on',
  created_at         DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 4.2 `dining_tables`

```sql
CREATE TABLE dining_tables (
  id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id             BIGINT UNSIGNED NOT NULL,
  no                  VARCHAR(16)  NOT NULL COMMENT '短编号，同时是小程序码 scene',
  zone                VARCHAR(32)  NOT NULL DEFAULT '',
  seats               INT UNSIGNED NOT NULL DEFAULT 4,
  status              VARCHAR(20)  NOT NULL DEFAULT 'idle',
  current_session_id  BIGINT UNSIGNED NULL,
  qr_file_id          VARCHAR(255) NOT NULL DEFAULT '',
  qr_generated_at     DATETIME(3)  NULL,
  sort                INT NOT NULL DEFAULT 0,
  created_at          DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (id),
  UNIQUE KEY uk_shop_no (shop_id, no),
  KEY idx_shop_status (shop_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

> `no` 修改后旧桌码立即失效，`qr_file_id` 必须置空重新生成——写业务代码时不能忘。

### 4.3 `table_sessions`

```sql
CREATE TABLE table_sessions (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id      BIGINT UNSIGNED NOT NULL,
  table_id     BIGINT UNSIGNED NOT NULL,
  table_no     VARCHAR(16) NOT NULL,
  status       VARCHAR(20) NOT NULL DEFAULT 'open',
  open_at      DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  expire_at    DATETIME(3) NOT NULL,
  close_at     DATETIME(3) NULL,
  order_count  INT UNSIGNED NOT NULL DEFAULT 0,
  paid_cents   BIGINT UNSIGNED NOT NULL DEFAULT 0,
  closed_by    VARCHAR(64) NOT NULL DEFAULT '',
  PRIMARY KEY (id),
  KEY idx_table_status (table_id, status),
  KEY idx_shop_status (shop_id, status),
  KEY idx_expire (expire_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

**关键约束**：同一 `table_id` 同时最多一条 `status='open'`。MySQL 无法用部分索引表达，靠「开台时先查后插 + 事务 + 唯一辅助列」实现。落地做法：加一列 `open_flag`，`open` 时为 `table_id`，关闭时置 `NULL`，配唯一索引即可：

```sql
ALTER TABLE table_sessions ADD COLUMN open_flag BIGINT UNSIGNED
  GENERATED ALWAYS AS (IF(status='open', table_id, NULL)) STORED,
  ADD UNIQUE KEY uk_table_open (open_flag);
```

这样就算并发两次开台，数据库也会直接拒绝第二条。

### 4.4 `categories` / 4.5 `dishes`

```sql
CREATE TABLE categories (
  id       BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id  BIGINT UNSIGNED NOT NULL,
  name     VARCHAR(32) NOT NULL,
  sort     INT NOT NULL DEFAULT 0,
  status   VARCHAR(20) NOT NULL DEFAULT 'on',
  PRIMARY KEY (id),
  KEY idx_shop_sort (shop_id, sort)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE dishes (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id      BIGINT UNSIGNED NOT NULL,
  category_id  BIGINT UNSIGNED NOT NULL,
  name         VARCHAR(64)  NOT NULL,
  description  VARCHAR(255) NOT NULL DEFAULT '',
  image_url    VARCHAR(255) NOT NULL DEFAULT '',
  price_cents  BIGINT UNSIGNED NOT NULL,
  unit         VARCHAR(16)  NOT NULL DEFAULT '份',
  specs        JSON NULL COMMENT '[{"name":"规格","options":[{"name":"大份","deltaCents":2000}]}]',
  addons       JSON NULL COMMENT '[{"name":"加香菜","deltaCents":0,"multiple":true}]',
  tags         JSON NULL,
  sold_out     TINYINT(1) NOT NULL DEFAULT 0,
  status       VARCHAR(20) NOT NULL DEFAULT 'on',
  sort         INT NOT NULL DEFAULT 0,
  sold_count   INT UNSIGNED NOT NULL DEFAULT 0,
  updated_at   DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  PRIMARY KEY (id),
  KEY idx_shop_cat_sort (shop_id, category_id, sort)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

> 规格与加料用 JSON 而不是拆表：它们是强耦合的小结构，几乎不单独查询，拆表只会让读写变复杂。MySQL 8 的 JSON 支持足够。

### 4.6 `orders`

```sql
CREATE TABLE orders (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id         BIGINT UNSIGNED NOT NULL,
  order_no        VARCHAR(32) NOT NULL COMMENT '展示单号，如 01-20261116-0007',
  session_id      BIGINT UNSIGNED NOT NULL,
  table_id        BIGINT UNSIGNED NOT NULL,
  table_no        VARCHAR(16) NOT NULL COMMENT '快照',
  seq             INT UNSIGNED NOT NULL DEFAULT 1 COMMENT '会话内第几单',
  status          VARCHAR(20) NOT NULL DEFAULT 'pending_pay',
  items_count     INT UNSIGNED NOT NULL DEFAULT 0,
  total_cents     BIGINT UNSIGNED NOT NULL DEFAULT 0,
  paid_cents      BIGINT UNSIGNED NOT NULL DEFAULT 0,
  refunded_cents  BIGINT UNSIGNED NOT NULL DEFAULT 0,
  remark          VARCHAR(255) NOT NULL DEFAULT '',
  guest_openid    VARCHAR(64) NOT NULL,
  client_token    VARCHAR(64) NOT NULL COMMENT '下单幂等键',
  cancel_reason   VARCHAR(100) NOT NULL DEFAULT '',
  created_at      DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  paid_at         DATETIME(3) NULL,
  accepted_at     DATETIME(3) NULL,
  served_at       DATETIME(3) NULL,
  closed_at       DATETIME(3) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_order_no (order_no),
  UNIQUE KEY uk_client_token (client_token),
  KEY idx_session (session_id),
  KEY idx_shop_created (shop_id, created_at),
  KEY idx_status_created (status, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 4.7 `order_items`

```sql
CREATE TABLE order_items (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  order_id         BIGINT UNSIGNED NOT NULL,
  shop_id          BIGINT UNSIGNED NOT NULL,
  dish_id          BIGINT UNSIGNED NOT NULL,
  dish_name        VARCHAR(64)  NOT NULL COMMENT '快照',
  image_url        VARCHAR(255) NOT NULL DEFAULT '',
  spec_text        VARCHAR(100) NOT NULL DEFAULT '',
  addons_text      VARCHAR(100) NOT NULL DEFAULT '',
  unit_price_cents BIGINT UNSIGNED NOT NULL COMMENT '含规格与加料加价',
  qty              INT UNSIGNED NOT NULL,
  amount_cents     BIGINT UNSIGNED NOT NULL,
  remark           VARCHAR(100) NOT NULL DEFAULT '',
  status           VARCHAR(20) NOT NULL DEFAULT 'normal',
  PRIMARY KEY (id),
  KEY idx_order (order_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 4.8 `payments` / 4.9 `refunds`

```sql
CREATE TABLE payments (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id        BIGINT UNSIGNED NOT NULL,
  order_id       BIGINT UNSIGNED NOT NULL,
  out_trade_no   VARCHAR(32) NOT NULL,
  transaction_id VARCHAR(64) NOT NULL DEFAULT '',
  amount_cents   BIGINT UNSIGNED NOT NULL,
  status         VARCHAR(20) NOT NULL DEFAULT 'created',
  prepay_id      VARCHAR(128) NOT NULL DEFAULT '',
  raw_callback   JSON NULL,
  created_at     DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  paid_at        DATETIME(3) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_out_trade_no (out_trade_no),
  KEY idx_order (order_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE refunds (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id         BIGINT UNSIGNED NOT NULL,
  order_id        BIGINT UNSIGNED NOT NULL,
  payment_id      BIGINT UNSIGNED NOT NULL,
  out_refund_no   VARCHAR(32) NOT NULL,
  amount_cents    BIGINT UNSIGNED NOT NULL,
  reason          VARCHAR(100) NOT NULL DEFAULT '',
  status          VARCHAR(20) NOT NULL DEFAULT 'pending',
  operator_openid VARCHAR(64) NOT NULL DEFAULT '',
  created_at      DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  finished_at     DATETIME(3) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_out_refund_no (out_refund_no),
  KEY idx_order (order_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 4.10 `staff` / 4.11 `staff_applications`

```sql
CREATE TABLE staff (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id     BIGINT UNSIGNED NOT NULL,
  openid      VARCHAR(64) NOT NULL,
  name        VARCHAR(32) NOT NULL,
  role        VARCHAR(20) NOT NULL DEFAULT 'waiter',
  status      VARCHAR(20) NOT NULL DEFAULT 'active',
  created_at  DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (id),
  UNIQUE KEY uk_openid (openid),
  KEY idx_shop (shop_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE staff_applications (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id     BIGINT UNSIGNED NOT NULL,
  openid      VARCHAR(64) NOT NULL,
  name        VARCHAR(32) NOT NULL,
  role        VARCHAR(20) NOT NULL DEFAULT 'waiter',
  status      VARCHAR(20) NOT NULL DEFAULT 'pending',
  reviewed_by VARCHAR(64) NOT NULL DEFAULT '',
  created_at  DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  reviewed_at DATETIME(3) NULL,
  PRIMARY KEY (id),
  KEY idx_shop_status (shop_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 4.12 `printers` / 4.13 `print_tasks`

```sql
CREATE TABLE printers (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id           BIGINT UNSIGNED NOT NULL,
  name              VARCHAR(32) NOT NULL,
  brand             VARCHAR(20) NOT NULL COMMENT 'feie / yilianyun',
  sn                VARCHAR(64) NOT NULL,
  secret_cipher     VARCHAR(255) NOT NULL COMMENT 'AES 加密存，禁止明文',
  paper_width       TINYINT UNSIGNED NOT NULL DEFAULT 58,
  bind_category_ids JSON NULL,
  status            VARCHAR(20) NOT NULL DEFAULT 'on',
  PRIMARY KEY (id),
  KEY idx_shop (shop_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE print_tasks (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  shop_id     BIGINT UNSIGNED NOT NULL,
  order_id    BIGINT UNSIGNED NOT NULL,
  printer_id  BIGINT UNSIGNED NULL,
  type        VARCHAR(20) NOT NULL DEFAULT 'new_order',
  content     TEXT NOT NULL,
  status      VARCHAR(20) NOT NULL DEFAULT 'pending',
  retry_count TINYINT UNSIGNED NOT NULL DEFAULT 0,
  last_error  VARCHAR(255) NOT NULL DEFAULT '',
  created_at  DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (id),
  KEY idx_status_created (status, created_at),
  KEY idx_order (order_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 4.14 `order_seq`

```sql
CREATE TABLE order_seq (
  shop_id  BIGINT UNSIGNED NOT NULL,
  biz_date DATE NOT NULL,
  seq      INT UNSIGNED NOT NULL DEFAULT 0,
  PRIMARY KEY (shop_id, biz_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

取号（原子、并发安全）：

```sql
INSERT INTO order_seq (shop_id, biz_date, seq) VALUES (?, CURDATE(), 1)
  ON DUPLICATE KEY UPDATE seq = seq + 1;
SELECT seq FROM order_seq WHERE shop_id = ? AND biz_date = CURDATE();
```

在同一个事务内完成，插入与查询之间不会被其他事务插队拿到同一个 `seq`（受主键行锁保护）。

---

## 五、事务与并发

### 5.1 下单

```sql
START TRANSACTION;
  -- 1. 取号（见上）
  -- 2. 校验会话仍为 open：条件更新拿行数
  SELECT id FROM table_sessions WHERE id = ? AND status = 'open' AND expire_at > NOW() FOR UPDATE;
  -- 3. INSERT orders（client_token 唯一索引兜底幂等）
  -- 4. 批量 INSERT order_items
  UPDATE table_sessions SET order_count = order_count + 1 WHERE id = ?;
COMMIT;
```

`client_token` 命中唯一索引（错误码 1062）时，**不要报错**：回查该 token 对应的订单直接返回——这才是移动端需要的幂等语义（用户连点两下不该看到错误）。

### 5.2 支付回调（幂等核心）

```sql
START TRANSACTION;
  UPDATE payments SET status='success', transaction_id=?, paid_at=NOW(3), raw_callback=?
    WHERE out_trade_no=? AND status='created';
  -- affectedRows = 0 → 已处理过，直接 COMMIT 返回成功
  UPDATE orders SET status='paid', paid_cents=?, paid_at=NOW(3)
    WHERE id=? AND status='pending_pay';
  -- affectedRows = 0 → 状态已被并发改掉，记告警
  UPDATE table_sessions SET paid_cents = paid_cents + ? WHERE id=?;
COMMIT;
-- 提交成功后才推 WebSocket、写 print_tasks
```

**顺序很重要**：推送和打印必须在事务提交之后，否则事务回滚会导致「管理端响了铃，但库里没有单」。

### 5.3 状态流转一律用条件更新

```sql
UPDATE orders SET status='accepted', accepted_at=NOW(3)
  WHERE id=? AND status='paid';
```

`affectedRows = 0` 即表示状态不允许，返回 `ORDER_STATUS_INVALID`。禁止先 `SELECT` 再无条件 `UPDATE`——那是并发下丢状态的经典写法。

### 5.4 连接池与超时

- `MySqlConnector` 连接池（驱动内建，`Pooling=true` 默认开启），`MaximumPoolSize=20`、`ConnectionTimeout=5`（秒）、`DefaultCommandTimeout=5`
- 生产连接串加 `SslMode=None` 仅在 MySQL 只监听 `127.0.0.1` 时可接受；若数据库与站点分离，必须开 TLS
- 单次查询超时 5 秒，超时记错误日志并抛 `SERVER_ERROR`
- 事务内禁止跨 `await` 调用外部 HTTP（微信支付、打印机），否则长事务会锁表
- 连接串只放 `appsettings.Production.json` 或环境变量，不写进代码；日志里出现连接串一律打码

---

## 六、金额计算规则（唯一真相在服务端）

```
单品实付价 = price_cents + Σ(规格选项 deltaCents) + Σ(加料 deltaCents)
行金额     = 单品实付价 × qty
订单总额   = Σ 行金额
```

`createOrder` 必须按 `dish_id` 重新读 `dishes` 算价，**忽略客户端传来的任何价格**。若客户端传了 `expectedTotalCents` 且不一致，返回 `PRICE_CHANGED` 并附差异明细。

---

## 七、容量与归档

单店日均 200 单、每单 8 个菜品：

| 表 | 日增 | 年增 | 处理 |
|---|---|---|---|
| `orders` | 200 | 7.3 万 | 1 年后按月归档到 `orders_archive_yyyyMM` |
| `order_items` | 1600 | 58 万 | 随订单归档 |
| `payments` | 200 | 7.3 万 | 不归档（对账需要） |
| `print_tasks` | 200 | 7.3 万 | 保留 90 天，定时清理 |
| 其他 | 极少 | — | 无需处理 |

单表百万级以内 MySQL 无压力，但所有查询必须带索引，禁止无条件全表扫描。

---

## 八、备份与恢复

| 项 | 方案 |
|---|---|
| 全量备份 | 每日 03:00 `mysqldump --single-transaction`，压缩后保留 30 天 |
| 增量 | 开启 binlog（`ROW` 格式），保留 7 天 |
| 异地 | 备份文件同步一份到对象存储或另一台机器 |
| 恢复演练 | **上线前必须做一次**：在测试库还原，确认订单数据完整 |
| 慢查询 | 开启 `slow_query_log`，阈值 1 秒，每周看一次 |

---

## 九、.NET 侧类型映射与数据访问约定

数据访问技术栈：**Dapper + MySqlConnector**（不用 EF Core，原因见 `00-overview.md` 4.6）。DDL 是唯一真相，实体类是它的投影，不允许 ORM 反向生成表结构。

### 9.1 列类型 → C# 类型

| MySQL | C# | 说明 |
|---|---|---|
| `BIGINT UNSIGNED` | `ulong` | 主键与外键统一用这个，别用 `int` |
| `INT UNSIGNED` | `uint` | 计数类字段 |
| `TINYINT(1)` | `bool` | MySqlConnector 默认 `TreatTinyAsBoolean=true`；**若连接串关掉了这个开关，会变成 `sbyte`，1 读出来是 `true` 还是 `1` 全看运气** |
| `VARCHAR(n)` / `TEXT` | `string` | 长度为 0 时返回空串而非 `null`，判空要写 `string.IsNullOrEmpty` |
| `DATETIME(3)` | `DateTime` | **Kind 是 `Unspecified`，必须手工标记为 UTC**（见 9.3） |
| `JSON` | `string` | Dapper 不会自动反序列化，在 Core 层用 `JsonSerializer` 转 |
| `*_cents` 全部金额 | `long` | 整数分，禁止 `decimal`、禁止 `double` |

### 9.2 Dapper 的三条全局设置

在 `Program.cs` 启动时设置一次，全项目生效：

```csharp
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;  // order_no -> OrderNo
Dapper.SqlMapper.AddTypeHandler(new UtcDateTimeHandler()); // 见 9.3
```

1. **`MatchNamesWithUnderscores = true`**：不设的话 `order_no` 映射不到 `OrderNo`，查询静默返回 0 或 null，编译期毫无提示——这是 Dapper 最坑的一点。
2. **禁止 `SELECT *`**：显式列名。加字段时多返回一列不会报错，但会让「接口返回了不该返回的字段」这种问题查半天。
3. **所有查询传 `CancellationToken`**，用 `CommandDefinition`：

```csharp
var cmd = new CommandDefinition(sql, param, transaction: tx,
              commandTimeout: 5, cancellationToken: ct);
var rows = await conn.QueryAsync<OrderRow>(cmd);
```

### 9.3 UTC 时间处理（必须做，否则差 8 小时）

`DATETIME` 不存时区。MySQL 驱动读出来是 `DateTimeKind.Unspecified`，`System.Text.Json` 序列化时**不会加 `Z`**，小程序 `new Date("2026-11-17T03:32:00")` 会当成本地时间解析——结果就是所有时间显示晚 8 小时。

```csharp
public sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override DateTime Parse(object value) =>
        DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);

    public override void SetValue(IDbDataParameter p, DateTime value) =>
        p.Value = value.ToUniversalTime();
}
```

配套约定：
- 写库一律 `DateTime.UtcNow`（禁止 `DateTime.Now`）
- SQL 里用 `UTC_TIMESTAMP(3)`，禁止 `NOW()`（`NOW()` 受服务器时区影响，换台机器就错）
- 出参统一 `DateTimeOffset` 转 `+08:00` 再序列化

### 9.4 JSON 列

`specs`、`addons`、`biz_hours`、`raw_callback` 这几个字段在 C# 里是 `string`：

```csharp
// 读
var specs = JsonSerializer.Deserialize<List<SpecGroup>>(row.Specs, JsonOpts);
// 写
p.Specs = JsonSerializer.Serialize(specs, JsonOpts);
```

- 统一用一份 `JsonSerializerOptions`：`PropertyNamingPolicy = CamelCase`、`DefaultIgnoreCondition = WhenWritingNull`（与 API 层 camelCase 保持一致，前端不用两套字段名）
- .NET 10 建议启用源生成 `JsonSerializerContext`，冷启动更快；IIS 进程回收后第一次请求的 JIT 开销也能省一点
- **不要把 JSON 列写进 `WHERE` 条件里做业务查询**：能用的索引只有函数索引，且写法一错就全表扫

### 9.5 事务与隔离级别

```csharp
await using var conn = await _factory.OpenAsync(ct);
await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
try
{
    // ... 多条 Dapper 调用，全部传 transaction: tx
    await tx.CommitAsync(ct);
}
catch { await tx.RollbackAsync(ct); throw; }
```

两个关键点：

1. **显式用 `ReadCommitted`**。MySQL 默认是 `REPEATABLE READ`，会用间隙锁（gap lock）。我们的 `order_seq` 取号、`table_sessions` 开台都是「同一行/同一区间的高频写入」，在 RR 下死锁概率明显更高。改 RC 后死锁基本消失。
2. **死锁要重试一次**。即使 RC，`orders` 与 `order_items` 的插入顺序不一致仍可能 1213：

```csharp
catch (MySqlException ex) when (ex.Number == 1213)  // Deadlock found
{
    await Task.Delay(50, ct);
    return await DoCreateOrderAsync(req, ct);       // 重试一次，靠 clientToken 唯一索引保证不重复
}
```

### 9.6 迁移脚本管理

```
server/db/
├─ 001_init.sql            14 张表 + 索引
├─ 002_seed_shop.sql       门店/管理员/示例分类
└─ 003_xxx.sql             后续变更，只增不改
```

- 文件名 `序号_说明.sql`，**按序号全量执行即可幂等**（每段用 `CREATE TABLE IF NOT EXISTS`、`ADD COLUMN IF NOT EXISTS`）
- 不用 EF Migrations 生成 DDL：生成列 `open_flag`、唯一索引 `uk_table_open` 这类写法 EF 表达不好，容易在两个环境跑出不同结果
- 生产执行前先在本地 MySQL 8.0.46 上跑一遍，再在测试库跑一遍

