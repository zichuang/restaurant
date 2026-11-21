-- =============================================================
-- 餐厅扫码点菜系统 · 演示数据
-- 用法：mysql -u root -p restaurant < 002-seed.sql
--
-- 说明：
--   1. 全部使用 INSERT IGNORE + 显式主键，可重复执行
--   2. 这是开发/演示数据，生产环境请通过管理端录入
--   3. 价格单位是「分」：3800 = 38.00 元
-- =============================================================

USE `restaurant`;
SET NAMES utf8mb4;
SET time_zone = '+08:00';


-- -------------------------------------------------------------
-- 店铺
-- -------------------------------------------------------------
INSERT IGNORE INTO `shops`
  (`id`, `name`, `address`, `phone`, `notice`, `biz_hours`,
   `session_ttl_minutes`, `auto_close_minutes`, `order_limit_cents`, `status`)
VALUES
  (1, '老程川菜馆', '成都市青羊区宽窄巷子 12 号', '028-8888 6666',
   '本店所有菜品现炒现做，出餐约 15 分钟。如需忌口请备注。',
   '[{"start":"10:30","end":"14:00"},{"start":"17:00","end":"21:30"}]',
   180, 15, 200000, 'on');


-- -------------------------------------------------------------
-- 桌台（no 即小程序码 scene，务必短且唯一）
-- -------------------------------------------------------------
INSERT IGNORE INTO `dining_tables`
  (`id`, `shop_id`, `no`, `zone`, `seats`, `status`, `sort`)
VALUES
  (1, 1, 'A01', '大厅', 4,  'idle', 10),
  (2, 1, 'A02', '大厅', 4,  'idle', 20),
  (3, 1, 'A03', '大厅', 2,  'idle', 30),
  (4, 1, 'A04', '大厅', 6,  'idle', 40),
  (5, 1, 'B01', '包间', 10, 'idle', 50),
  (6, 1, 'B02', '包间', 8,  'idle', 60),
  (7, 1, 'C01', '露台', 4,  'idle', 70),
  (8, 1, 'C02', '露台', 4,  'idle', 80);


-- -------------------------------------------------------------
-- 分类
-- -------------------------------------------------------------
INSERT IGNORE INTO `categories` (`id`, `shop_id`, `name`, `sort`, `status`) VALUES
  (1, 1, '招牌推荐', 10, 'on'),
  (2, 1, '热菜',     20, 'on'),
  (3, 1, '凉菜',     30, 'on'),
  (4, 1, '汤品',     40, 'on'),
  (5, 1, '主食',     50, 'on'),
  (6, 1, '饮品',     60, 'on');


-- -------------------------------------------------------------
-- 菜品
--   specs: 单选规格，deltaCents 为加价
--   addons: 可多选加料
-- -------------------------------------------------------------
INSERT IGNORE INTO `dishes`
  (`id`, `shop_id`, `category_id`, `name`, `description`, `price_cents`, `unit`,
   `specs`, `addons`, `tags`, `sold_out`, `status`, `sort`)
VALUES
  -- 招牌推荐
  (1, 1, 1, '水煮鱼', '新鲜草鱼片，秘制红油，麻辣鲜香',
   6800, '份',
   '[{"name":"规格","options":[{"name":"小份","deltaCents":0},{"name":"大份","deltaCents":3000}]},{"name":"辣度","options":[{"name":"微辣","deltaCents":0},{"name":"中辣","deltaCents":0},{"name":"特辣","deltaCents":0}]}]',
   '[{"name":"加豆芽","deltaCents":500,"multiple":false}]',
   '["招牌","麻辣"]', 0, 'on', 10),

  (2, 1, 1, '夫妻肺片', '牛肉牛杂拌红油，麻辣爽口',
   3800, '份',
   NULL,
   '[{"name":"加香菜","deltaCents":0,"multiple":false}]',
   '["招牌","凉菜"]', 0, 'on', 20),

  (3, 1, 1, '麻婆豆腐', '嫩豆腐配牛肉末，一勺下三碗饭',
   2800, '份',
   '[{"name":"辣度","options":[{"name":"微辣","deltaCents":0},{"name":"中辣","deltaCents":0}]}]',
   NULL,
   '["招牌"]', 0, 'on', 30),

  -- 热菜
  (4, 1, 2, '回锅肉', '二刀肉配蒜苗，肥而不腻',
   4200, '份',
   NULL, NULL, '["下饭"]', 0, 'on', 10),

  (5, 1, 2, '宫保鸡丁', '鸡腿肉配花生米，酸甜微辣',
   3600, '份',
   '[{"name":"规格","options":[{"name":"小份","deltaCents":0},{"name":"大份","deltaCents":1200}]}]',
   '[{"name":"加花生","deltaCents":300,"multiple":false}]',
   '["下饭"]', 0, 'on', 20),

  (6, 1, 2, '干煸四季豆', '煸至起皱，配干辣椒与肉末',
   2600, '份',
   NULL, NULL, NULL, 0, 'on', 30),

  (7, 1, 2, '鱼香肉丝', '经典鱼香味，酸甜微辣',
   3200, '份',
   NULL, NULL, '["下饭"]', 0, 'on', 40),

  -- 凉菜
  (8, 1, 3, '口水鸡', '土鸡腿撕丝，藤椒红油',
   3600, '份',
   NULL,
   '[{"name":"加花生碎","deltaCents":300,"multiple":false},{"name":"加香菜","deltaCents":0,"multiple":false}]',
   '["凉菜"]', 0, 'on', 10),

  (9, 1, 3, '蒜泥白肉', '薄切五花，蒜香浓郁',
   3400, '份',
   NULL, NULL, NULL, 0, 'on', 20),

  -- 汤品
  (10, 1, 4, '酸辣汤', '开胃解腻，胡椒足',
   1800, '份',
   '[{"name":"规格","options":[{"name":"一位","deltaCents":0},{"name":"一例","deltaCents":1200}]}]',
   NULL, NULL, 0, 'on', 10),

  (11, 1, 4, '番茄蛋汤', '清爽酸甜',
   1600, '份',
   '[{"name":"规格","options":[{"name":"一位","deltaCents":0},{"name":"一例","deltaCents":1000}]}]',
   NULL, NULL, 0, 'on', 20),

  -- 主食
  (12, 1, 5, '米饭', '东北五常大米',
   300, '碗',
   NULL, NULL, NULL, 0, 'on', 10),

  (13, 1, 5, '担担面', '芝麻酱底，微辣',
   1600, '碗',
   '[{"name":"辣度","options":[{"name":"不辣","deltaCents":0},{"name":"微辣","deltaCents":0},{"name":"中辣","deltaCents":0}]}]',
   '[{"name":"加青菜","deltaCents":300,"multiple":false}]',
   '["主食"]', 0, 'on', 20),

  -- 饮品
  (14, 1, 6, '冰粉', '红糖山楂花生碎',
   1000, '份', NULL, NULL, '["解辣"]', 0, 'on', 10),

  (15, 1, 6, '鲜榨橙汁', '当日现榨，约 300ml',
   1500, '杯',
   '[{"name":"温度","options":[{"name":"常温","deltaCents":0},{"name":"加冰","deltaCents":0}]}]',
   NULL, NULL, 0, 'on', 20),

  (16, 1, 6, '王老吉', '罐装 310ml',
   800, '罐', NULL, NULL, NULL, 0, 'on', 30);


-- =============================================================
-- 验证
-- =============================================================
SELECT '店铺' AS t, COUNT(*) AS c FROM shops
UNION ALL SELECT '桌台', COUNT(*) FROM dining_tables
UNION ALL SELECT '分类', COUNT(*) FROM categories
UNION ALL SELECT '菜品', COUNT(*) FROM dishes;
