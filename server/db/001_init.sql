-- =============================================================
-- 餐厅扫码点菜系统 · 数据库初始化脚本
-- 目标库：MySQL 8.0+
-- 字符集：utf8mb4 / utf8mb4_0900_ai_ci
--
-- 用法：
--   mysql -u root -p < 001-schema.sql
--
-- 说明：
--   1. 本脚本幂等（CREATE DATABASE/TABLE IF NOT EXISTS），可重复执行
--   2. 金额一律 *Cents（BIGINT UNSIGNED，单位：分），禁止浮点
--   3. 时间一律 DATETIME(3)，存本地时区（Asia/Shanghai）
--   4. 不使用外键约束，完整性由应用层事务保证（见 02-data-model.md 第五节）
-- =============================================================

CREATE DATABASE IF NOT EXISTS `restaurant`
  DEFAULT CHARACTER SET utf8mb4
  DEFAULT COLLATE utf8mb4_0900_ai_ci;

USE `restaurant`;

SET NAMES utf8mb4;
SET time_zone = '+08:00';


-- -------------------------------------------------------------
-- 4.1 shops 店铺
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `shops` (
  `id`                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `name`                VARCHAR(64)     NOT NULL,
  `logo_url`            VARCHAR(255)    NOT NULL DEFAULT '',
  `address`             VARCHAR(255)    NOT NULL DEFAULT '',
  `phone`               VARCHAR(32)     NOT NULL DEFAULT '',
  `notice`              VARCHAR(255)    NOT NULL DEFAULT '',
  `biz_hours`           JSON            NULL COMMENT '[{"start":"10:00","end":"21:30"}]',
  `session_ttl_minutes` INT UNSIGNED    NOT NULL DEFAULT 180 COMMENT '桌台会话有效期（分钟）',
  `auto_close_minutes`  INT UNSIGNED    NOT NULL DEFAULT 15  COMMENT '空闲多久自动关台',
  `order_limit_cents`   BIGINT UNSIGNED NOT NULL DEFAULT 200000 COMMENT '单笔限额，防桌码盗用',
  `status`              VARCHAR(20)     NOT NULL DEFAULT 'on',
  `created_at`          DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='店铺';


-- -------------------------------------------------------------
-- 4.2 dining_tables 桌台
--     no 即小程序码 scene，必须短（<=16 且 URL 安全），如 A01
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `dining_tables` (
  `id`                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`            BIGINT UNSIGNED NOT NULL,
  `no`                 VARCHAR(16)     NOT NULL COMMENT '短编号，同时是小程序码 scene',
  `zone`               VARCHAR(32)     NOT NULL DEFAULT '' COMMENT '区域：大厅/包间/露台',
  `seats`              INT UNSIGNED    NOT NULL DEFAULT 4,
  `status`             VARCHAR(20)     NOT NULL DEFAULT 'idle' COMMENT 'idle/occupied/disabled',
  `current_session_id` BIGINT UNSIGNED NULL,
  `qr_file_id`         VARCHAR(255)    NOT NULL DEFAULT '',
  `qr_generated_at`    DATETIME(3)     NULL,
  `sort`               INT             NOT NULL DEFAULT 0,
  `created_at`         DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_shop_no` (`shop_id`, `no`),
  KEY `idx_shop_status` (`shop_id`, `status`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='桌台';


-- -------------------------------------------------------------
-- 4.3 table_sessions 桌台会话
--     一次开台 = 一条记录，订单挂会话而非桌号（防桌号复用串单）
--     open_flag 生成列 + 唯一索引，保证同桌同时只有一条 open
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `table_sessions` (
  `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`     BIGINT UNSIGNED NOT NULL,
  `table_id`    BIGINT UNSIGNED NOT NULL,
  `table_no`    VARCHAR(16)     NOT NULL COMMENT '快照，改桌号不影响历史',
  `status`      VARCHAR(20)     NOT NULL DEFAULT 'open' COMMENT 'open/closed',
  `open_at`     DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `expire_at`   DATETIME(3)     NOT NULL,
  `close_at`    DATETIME(3)     NULL,
  `order_count` INT UNSIGNED    NOT NULL DEFAULT 0,
  `paid_cents`  BIGINT UNSIGNED NOT NULL DEFAULT 0,
  `closed_by`   VARCHAR(64)     NOT NULL DEFAULT '',
  `open_flag`   BIGINT UNSIGNED GENERATED ALWAYS AS (IF(`status` = 'open', `table_id`, NULL)) STORED
                                COMMENT '并发开台互斥锁：open 时等于 table_id，关闭置 NULL',
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_table_open` (`open_flag`),
  KEY `idx_table_status` (`table_id`, `status`),
  KEY `idx_shop_status` (`shop_id`, `status`),
  KEY `idx_expire` (`expire_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='桌台会话';


-- -------------------------------------------------------------
-- 4.4 categories 菜品分类
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `categories` (
  `id`      BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id` BIGINT UNSIGNED NOT NULL,
  `name`    VARCHAR(32)     NOT NULL,
  `sort`    INT             NOT NULL DEFAULT 0,
  `status`  VARCHAR(20)     NOT NULL DEFAULT 'on',
  PRIMARY KEY (`id`),
  KEY `idx_shop_sort` (`shop_id`, `sort`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='菜品分类';


-- -------------------------------------------------------------
-- 4.5 dishes 菜品
--     specs / addons 用 JSON：强耦合小结构，几乎不单独查询
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `dishes` (
  `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`     BIGINT UNSIGNED NOT NULL,
  `category_id` BIGINT UNSIGNED NOT NULL,
  `name`        VARCHAR(64)     NOT NULL,
  `description` VARCHAR(255)    NOT NULL DEFAULT '',
  `image_url`   VARCHAR(255)    NOT NULL DEFAULT '',
  `price_cents` BIGINT UNSIGNED NOT NULL,
  `unit`        VARCHAR(16)     NOT NULL DEFAULT '份',
  `specs`       JSON            NULL COMMENT '[{"name":"规格","options":[{"name":"大份","deltaCents":2000}]}]',
  `addons`      JSON            NULL COMMENT '[{"name":"加香菜","deltaCents":0,"multiple":true}]',
  `tags`        JSON            NULL COMMENT '["招牌","微辣"]',
  `sold_out`    TINYINT(1)      NOT NULL DEFAULT 0 COMMENT '估清：临时卖完',
  `status`      VARCHAR(20)     NOT NULL DEFAULT 'on' COMMENT 'on/off/deleted',
  `sort`        INT             NOT NULL DEFAULT 0,
  `sold_count`  INT UNSIGNED    NOT NULL DEFAULT 0,
  `updated_at`  DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`id`),
  KEY `idx_shop_cat_sort` (`shop_id`, `category_id`, `sort`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='菜品';


-- -------------------------------------------------------------
-- 4.6 orders 订单
--     uk_client_token = 下单幂等键
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `orders` (
  `id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`        BIGINT UNSIGNED NOT NULL,
  `order_no`       VARCHAR(32)     NOT NULL COMMENT '展示单号，如 01-20261116-0007',
  `session_id`     BIGINT UNSIGNED NOT NULL,
  `table_id`       BIGINT UNSIGNED NOT NULL,
  `table_no`       VARCHAR(16)     NOT NULL COMMENT '快照',
  `seq`            INT UNSIGNED    NOT NULL DEFAULT 1 COMMENT '会话内第几单',
  `status`         VARCHAR(20)     NOT NULL DEFAULT 'pending_pay',
  `items_count`    INT UNSIGNED    NOT NULL DEFAULT 0,
  `total_cents`    BIGINT UNSIGNED NOT NULL DEFAULT 0,
  `paid_cents`     BIGINT UNSIGNED NOT NULL DEFAULT 0,
  `refunded_cents` BIGINT UNSIGNED NOT NULL DEFAULT 0,
  `remark`         VARCHAR(255)    NOT NULL DEFAULT '',
  `guest_openid`   VARCHAR(64)     NOT NULL,
  `client_token`   VARCHAR(64)     NOT NULL COMMENT '下单幂等键',
  `cancel_reason`  VARCHAR(100)    NOT NULL DEFAULT '',
  `created_at`     DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `paid_at`        DATETIME(3)     NULL,
  `accepted_at`    DATETIME(3)     NULL,
  `served_at`      DATETIME(3)     NULL,
  `closed_at`      DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_order_no` (`order_no`),
  UNIQUE KEY `uk_client_token` (`client_token`),
  KEY `idx_session` (`session_id`),
  KEY `idx_shop_created` (`shop_id`, `created_at`),
  KEY `idx_status_created` (`status`, `created_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='订单';


-- -------------------------------------------------------------
-- 4.7 order_items 订单明细（价格快照表）
--     菜品改价、改名、下架都不影响历史订单
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `order_items` (
  `id`               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `order_id`         BIGINT UNSIGNED NOT NULL,
  `shop_id`          BIGINT UNSIGNED NOT NULL,
  `dish_id`          BIGINT UNSIGNED NOT NULL,
  `dish_name`        VARCHAR(64)     NOT NULL COMMENT '快照',
  `image_url`        VARCHAR(255)    NOT NULL DEFAULT '',
  `spec_text`        VARCHAR(100)    NOT NULL DEFAULT '' COMMENT '如 大份',
  `addons_text`      VARCHAR(100)    NOT NULL DEFAULT '' COMMENT '如 加香菜、加辣',
  `unit_price_cents` BIGINT UNSIGNED NOT NULL COMMENT '含规格与加料加价',
  `qty`              INT UNSIGNED    NOT NULL,
  `amount_cents`     BIGINT UNSIGNED NOT NULL,
  `remark`           VARCHAR(100)    NOT NULL DEFAULT '',
  `status`           VARCHAR(20)     NOT NULL DEFAULT 'normal' COMMENT 'normal/refunded',
  PRIMARY KEY (`id`),
  KEY `idx_order` (`order_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='订单明细';


-- -------------------------------------------------------------
-- 4.8 payments 支付流水
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `payments` (
  `id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`        BIGINT UNSIGNED NOT NULL,
  `order_id`       BIGINT UNSIGNED NOT NULL,
  `out_trade_no`   VARCHAR(32)     NOT NULL,
  `transaction_id` VARCHAR(64)     NOT NULL DEFAULT '',
  `amount_cents`   BIGINT UNSIGNED NOT NULL,
  `status`         VARCHAR(20)     NOT NULL DEFAULT 'created' COMMENT 'created/success/closed/refunded',
  `prepay_id`      VARCHAR(128)    NOT NULL DEFAULT '',
  `raw_callback`   JSON            NULL COMMENT '原始回调报文，排障用',
  `created_at`     DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `paid_at`        DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_out_trade_no` (`out_trade_no`),
  KEY `idx_order` (`order_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='支付流水';


-- -------------------------------------------------------------
-- 4.9 refunds 退款单
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `refunds` (
  `id`              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`         BIGINT UNSIGNED NOT NULL,
  `order_id`        BIGINT UNSIGNED NOT NULL,
  `payment_id`      BIGINT UNSIGNED NOT NULL,
  `out_refund_no`   VARCHAR(32)     NOT NULL,
  `amount_cents`    BIGINT UNSIGNED NOT NULL,
  `reason`          VARCHAR(100)    NOT NULL DEFAULT '',
  `status`          VARCHAR(20)     NOT NULL DEFAULT 'pending' COMMENT 'pending/success/failed',
  `operator_openid` VARCHAR(64)     NOT NULL DEFAULT '',
  `created_at`      DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `finished_at`     DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_out_refund_no` (`out_refund_no`),
  KEY `idx_order` (`order_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='退款单';


-- -------------------------------------------------------------
-- 4.10 staff 员工
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `staff` (
  `id`         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`    BIGINT UNSIGNED NOT NULL,
  `openid`     VARCHAR(64)     NOT NULL,
  `name`       VARCHAR(32)     NOT NULL,
  `role`       VARCHAR(20)     NOT NULL DEFAULT 'waiter' COMMENT 'owner/manager/waiter/kitchen',
  `status`     VARCHAR(20)     NOT NULL DEFAULT 'active' COMMENT 'active/disabled',
  `created_at` DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_openid` (`openid`),
  KEY `idx_shop` (`shop_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='员工';


-- -------------------------------------------------------------
-- 4.11 staff_applications 员工申请（管理员审批后写入 staff）
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `staff_applications` (
  `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`     BIGINT UNSIGNED NOT NULL,
  `openid`      VARCHAR(64)     NOT NULL,
  `name`        VARCHAR(32)     NOT NULL,
  `role`        VARCHAR(20)     NOT NULL DEFAULT 'waiter',
  `status`      VARCHAR(20)     NOT NULL DEFAULT 'pending' COMMENT 'pending/approved/rejected',
  `reviewed_by` VARCHAR(64)     NOT NULL DEFAULT '',
  `created_at`  DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  `reviewed_at` DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  KEY `idx_shop_status` (`shop_id`, `status`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='员工申请';


-- -------------------------------------------------------------
-- 4.12 printers 打印机
--      secret_cipher 必须 AES 加密存储，禁止明文
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `printers` (
  `id`                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`           BIGINT UNSIGNED NOT NULL,
  `name`              VARCHAR(32)     NOT NULL,
  `brand`             VARCHAR(20)     NOT NULL COMMENT 'feie / yilianyun',
  `sn`                VARCHAR(64)     NOT NULL,
  `secret_cipher`     VARCHAR(255)    NOT NULL COMMENT 'AES 加密存，禁止明文',
  `paper_width`       TINYINT UNSIGNED NOT NULL DEFAULT 58,
  `bind_category_ids` JSON            NULL COMMENT '绑定分类，空表示全打',
  `status`            VARCHAR(20)     NOT NULL DEFAULT 'on',
  PRIMARY KEY (`id`),
  KEY `idx_shop` (`shop_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='打印机';


-- -------------------------------------------------------------
-- 4.13 print_tasks 打印任务队列
--      由后台服务轮询，失败重试，超过次数转人工
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `print_tasks` (
  `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `shop_id`     BIGINT UNSIGNED NOT NULL,
  `order_id`    BIGINT UNSIGNED NOT NULL,
  `printer_id`  BIGINT UNSIGNED NULL,
  `type`        VARCHAR(20)     NOT NULL DEFAULT 'new_order' COMMENT 'new_order/refund/test',
  `content`     TEXT            NOT NULL,
  `status`      VARCHAR(20)     NOT NULL DEFAULT 'pending' COMMENT 'pending/printing/done/failed',
  `retry_count` TINYINT UNSIGNED NOT NULL DEFAULT 0,
  `last_error`  VARCHAR(255)    NOT NULL DEFAULT '',
  `created_at`  DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (`id`),
  KEY `idx_status_created` (`status`, `created_at`),
  KEY `idx_order` (`order_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='打印任务';


-- -------------------------------------------------------------
-- 4.14 order_seq 单号序列
--      INSERT ... ON DUPLICATE KEY UPDATE 取号，天然并发安全
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `order_seq` (
  `shop_id`  BIGINT UNSIGNED NOT NULL,
  `biz_date` DATE            NOT NULL,
  `seq`      INT UNSIGNED    NOT NULL DEFAULT 0,
  PRIMARY KEY (`shop_id`, `biz_date`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='单号序列';


-- =============================================================
-- 验证：应输出 14 张表
-- =============================================================
SELECT COUNT(*) AS table_count FROM information_schema.tables
 WHERE table_schema = 'restaurant';
