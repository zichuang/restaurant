# Ubuntu 24.04 + systemd + Nginx 部署手册

> 版本 v0.4 · 2026-11-17
> 目标：一台 **Ubuntu 24.04 LTS**，运行 **ASP.NET Core 10（Kestrel）+ MySQL 8.0**，
> Nginx 终结 TLS 并反向代理，systemd 守护应用进程。
> 与 IIS 方案相比，本方案**没有应用池、没有 WebSocket 功能开关、没有 web.config**，相应的坑也不存在。

---

## 〇、开始前先确认这五件事

| # | 检查项 | 怎么看 | 不满足的后果 |
|---|---|---|---|
| 1 | 域名已备案、A 记录指向本机公网 IP | `ping 你的域名` | 小程序后台无法配服务器域名，请求全拦 |
| 2 | 有 `sudo` 权限 | `sudo -v` | 什么都装不了 |
| 3 | **系统时区是 Asia/Shanghai** | `timedatectl` | 订单时间差 8 小时，对账全乱（见第一节） |
| 4 | 80 / 443 未被占用 | `ss -lntp \| grep -E ':80\|:443'` | Nginx 起不来 |
| 5 | 内存 ≥ 2 GB（1 GB 需加 swap） | `free -h` | `dotnet publish` 和 MySQL 会 OOM |

选择 Ubuntu 24.04 的理由：微软官方 apt 源直接支持 .NET 10，MySQL 8.0 在官方仓库里，出问题社区资料最多。

---

## 一、系统初始化（四条命令，别跳）

```bash
# 1) 时区 —— 这一条是最容易被忽略、后果最隐蔽的
sudo timedatectl set-timezone Asia/Shanghai
timedatectl                      # 确认 Time zone: Asia/Shanghai (CST, +0800)

# 2) 时间同步 —— 服务器时间与微信支付偏差超过 5 分钟，签名直接报错
sudo systemctl enable --now systemd-timesyncd
timedatectl timesync-status      # 确认 Offset 在几十毫秒内

# 3) 更新系统
sudo apt update && sudo apt upgrade -y

# 4) 防火墙（先放行 22，否则开 ufw 会把自己锁在门外）
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw --force enable
sudo ufw status verbose
```

> **为什么时区排第一**：Linux 默认 UTC。不设时区时 `DateTime.Now` 拿到的是 UTC，写进 `orders.created_at` 会比北京时间早 8 小时。白天调接口一切正常，晚上对账才发现"这单怎么是凌晨下的"。后面 systemd 里还会再设一次 `TZ` 做双保险。

内存 1 GB 的机器补一个 swap：

```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

---

## 二、安装与配置 MySQL 8.0

### 2.1 安装

```bash
sudo apt install -y mysql-server
sudo systemctl enable --now mysql
mysql --version                  # 应为 8.0.x
```

Ubuntu 24.04 官方仓库的 MySQL 就是 8.0 系列，与本地开发机的 8.0.46 同一大版本，行为一致。

### 2.2 加固 `mysqld.cnf`

编辑 `/etc/mysql/mysql.conf.d/mysqld.cnf`，在 `[mysqld]` 段内确认或加入：

```ini
[mysqld]
bind-address            = 127.0.0.1
mysqlx-bind-address     = 127.0.0.1
character-set-server    = utf8mb4
collation-server        = utf8mb4_0900_ai_ci
default-time-zone       = '+08:00'
max_connections         = 100
slow_query_log          = 1
slow_query_log_file     = /var/log/mysql/slow.log
long_query_time         = 1
```

四点说明：

- **`bind-address = 127.0.0.1`** 是硬要求。数据库绝不能监听 `0.0.0.0`，公网 3306 是扫描器第一目标
- **`default-time-zone`** 与系统时区保持一致，避免 `NOW()` 和 C# 侧时间打架
- **`utf8mb4_0900_ai_ci`** 是 MySQL 8 默认排序规则，中文正常，不要再改成 `utf8mb4_general_ci`
- **慢查询日志**留着，上线后排"管理端桌况为什么慢"全靠它

```bash
sudo systemctl restart mysql
mysql -uroot -p -e "SELECT @@bind_address, @@character_set_server, @@default_time_zone;"
```

### 2.3 建库与最小权限账号

```bash
sudo mysql          # Ubuntu 的 root 默认走 auth_socket，sudo 下无需密码
```

```sql
CREATE DATABASE restaurant
  DEFAULT CHARACTER SET utf8mb4
  DEFAULT COLLATE utf8mb4_0900_ai_ci;

-- 应用账号：只给 DML，不给 DDL（生产环境别给 ALL）
CREATE USER 'rest_app'@'127.0.0.1' IDENTIFIED BY '换成强密码';
GRANT SELECT, INSERT, UPDATE, DELETE ON restaurant.* TO 'rest_app'@'127.0.0.1';
FLUSH PRIVILEGES;
```

> 生产库建表用 root 或单独的迁移账号跑 DDL，**应用账号永远不给 `DROP`/`ALTER`**。这样即使代码有注入漏洞，也删不掉表。

### 2.4 执行建表脚本

把 `02-data-model.md` 里的 DDL 存成 `schema.sql` 传上去：

```bash
scp schema.sql 用户@服务器:/tmp/
mysql -uroot -p restaurant < /tmp/schema.sql
mysql -uroot -p restaurant -e "SHOW TABLES;"     # 应看到 14 张表
```

---

## 三、安装 .NET 10 运行时

两种方式，**推荐 A**：

| 方式 | 命令量 | 升级方式 | 适用 |
|---|---|---|---|
| **A. 官方 apt 源**（推荐） | 4 条 | `apt upgrade` 自动跟安全补丁 | 常规部署 |
| B. 自包含发布 | 0 条（发布时打包运行时） | 每次重新发布 | 服务器不能加第三方源的场景 |

**方式 A：**

```bash
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/ms-prod.deb
sudo dpkg -i /tmp/ms-prod.deb
sudo apt update
sudo apt install -y aspnetcore-runtime-10.0
dotnet --list-runtimes        # 应出现 Microsoft.AspNetCore.App 10.0.x
```

依赖检查（**ICU 必须装**，否则应用启动直接崩）：

```bash
dpkg -l | grep -i libicu     # 应有 libicu74
```

> ICU 是 .NET 处理字符串比较与日期格式化的依赖。缺了会抛 `Couldn't find a valid ICU package`，应用起不来。可以用 `InvariantGlobalization=true` 绕过，但**中文排序和大小写转换会退化**——菜品分类要排序，别关它。

---

## 四、发布应用

### 4.1 本地打包（Windows 开发机）

```powershell
cd E:\WX\MiniProGrame\restaurant\server
dotnet publish src\Restaurant.Api\Restaurant.Api.csproj `
  -c Release -r linux-x64 --self-contained false `
  -o publish
```

> **不能带 `--self-contained true` 又装运行时**，二选一。我们走方式 A（装运行时），所以这里 `false`。

### 4.2 上传

```bash
ssh 用户@服务器 "sudo mkdir -p /var/www/restaurant-api"
scp -r publish/* 用户@服务器:/tmp/restaurant-publish/
ssh 用户@服务器 "sudo rsync -a --delete /tmp/restaurant-publish/ /var/www/restaurant-api/"
```

**不要用 `scp` 直接覆盖到 `/var/www/restaurant-api`**——文件传输中断会留下半个目录，站点直接挂。先传到 `/tmp` 再 `rsync` 是稳妥做法。

### 4.3 目录与权限

```bash
# 专用系统用户，不给登录 shell
sudo useradd -r -s /usr/sbin/nologin restaurant || true

# 应用目录：属主是运行用户
sudo chown -R restaurant:restaurant /var/www/restaurant-api

# 运行期需要写入的目录
sudo mkdir -p /var/www/restaurant-api/uploads
sudo chown -R restaurant:restaurant /var/www/restaurant-api/uploads

# 密钥目录：只读，连属主都不要写权限
sudo mkdir -p /var/www/restaurant-api/secrets
sudo chown -R root:restaurant /var/www/restaurant-api/secrets
sudo chmod 750 /var/www/restaurant-api/secrets
sudo chmod 640 /var/www/restaurant-api/secrets/*.pem

# 日志目录
sudo mkdir -p /var/log/restaurant
sudo chown -R restaurant:restaurant /var/log/restaurant
```

三个目录的权限逻辑不一样，别一把 `chmod -R 777` 了事：

| 目录 | 属主 | 权限 | 原因 |
|---|---|---|---|
| `/var/www/restaurant-api` | `restaurant` | 755 | 运行用户要读 DLL |
| `uploads/` | `restaurant` | 755 | 要写菜品图，要能被静态中间件读出 |
| `secrets/` | `root:restaurant` | 750 / 文件 640 | **只能读不能写**，防止被攻破后篡改私钥 |

---

## 五、systemd 服务（本方案的核心）

创建 `/etc/systemd/system/restaurant-api.service`：

```ini
[Unit]
Description=Restaurant API (ASP.NET Core 10)
After=network.target mysql.service
Wants=mysql.service

[Service]
Type=simple
WorkingDirectory=/var/www/restaurant-api
ExecStart=/usr/bin/dotnet /var/www/restaurant-api/Restaurant.Api.dll
User=restaurant
Group=restaurant
Restart=always
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=30
SyslogIdentifier=restaurant-api

# 只监听回环，外部一律走 Nginx
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=ASPNETCORE_ENVIRONMENT=Production
# 时区双保险：即使系统时区被人改错，进程仍是东八区
Environment=TZ=Asia/Shanghai
Environment=DOTNET_NOLOGO=1

[Install]
WantedBy=multi-user.target
```

启用：

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now restaurant-api
sudo systemctl status restaurant-api --no-pager
curl -s http://127.0.0.1:5000/api/ping
```

### 五个参数为什么这么设

| 参数 | 值 | 原因 |
|---|---|---|
| `Restart=always` | 必填 | 进程崩溃自动拉起。**这就是替代 IIS 应用池的东西** |
| `RestartSec=5` | 5 秒 | 崩溃循环时不要疯狂重启打满日志；5 秒足够避开瞬时故障 |
| `ASPNETCORE_URLS` | `127.0.0.1:5000` | 只绑回环。写成 `0.0.0.0` 就等于绕过 Nginx 直连 Kestrel，TLS 白配了 |
| `TZ` | `Asia/Shanghai` | 时区双保险，见第一节 |
| `TimeoutStopSec` | 30 秒 | 给 `BackgroundService` 和 WebSocket 优雅退出的时间（默认 90 秒太长，会拖慢发布） |

> `Type=simple` 是最稳的写法。如果想要 systemd 精确知道"应用真的就绪了"，可以在项目里加 `Microsoft.Extensions.Hosting.Systemd` 包、`builder.Host.UseSystemd()`，然后把 `Type` 改成 `notify`。不加也能跑，`simple` 只是把进程起来就算启动成功。

### 常用命令

```bash
sudo systemctl restart restaurant-api      # 重启（发布后必做）
sudo systemctl stop restaurant-api
sudo journalctl -u restaurant-api -f       # 跟日志
sudo journalctl -u restaurant-api --since "10 min ago" -p err
```

---

## 六、Nginx 反向代理与 HTTPS

### 6.1 安装与站点配置

```bash
sudo apt install -y nginx
sudo systemctl enable --now nginx
```

创建 `/etc/nginx/sites-available/restaurant-api`（**把 `api.example.com` 换成你的备案域名**）：

```nginx
server {
    listen 80;
    server_name api.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name api.example.com;

    ssl_certificate     /etc/letsencrypt/live/api.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/api.example.com/privkey.pem;
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_session_cache   shared:SSL:10m;

    server_tokens off;
    client_max_body_size 10m;

    # 普通接口
    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 60s;
    }

    # WebSocket 专用：缺 Upgrade 头会握手失败
    location /ws {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade    $http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_buffering    off;
    }
}
```

启用：

```bash
sudo ln -s /etc/nginx/sites-available/restaurant-api /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

### 6.2 `/ws` 那段为什么必须单写

这是整套配置里唯一"写错了很难查"的地方：

| 缺什么 | 现象 | 为什么难查 |
|---|---|---|
| `Upgrade` / `Connection` 头 | 握手返回 400 或 426，管理端永远收不到提醒 | 小程序端只报"连接失败"，服务端日志一片安静 |
| `proxy_read_timeout` 放大 | 连接**每 60 秒整断一次**，看起来像"随机掉线" | 服务端日志显示正常关闭，容易被误判成客户端问题 |
| `proxy_buffering off` | 消息延迟几秒才到，响铃变慢 | 功能是通的，只是慢，容易当成代码问题 |

`proxy_read_timeout` 默认 60 秒——**这是 WebSocket 反代最经典的坑**。Kestrel 每 30 秒发一次 ping，但只要 60 秒内没有业务数据，Nginx 就会主动切断。设成 3600 秒后由应用层心跳维持。

### 6.3 证书（Let's Encrypt，自动续期）

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d api.example.com
sudo certbot renew --dry-run         # 验证续期流程能跑通
```

certbot 会自动装一个 systemd timer（`certbot.timer`），默认每天检查两次、到期前 30 天续期，**不需要自己写 cron**。确认一下：

```bash
systemctl list-timers | grep certbot
```

> 证书到期当天小程序所有请求会全部失败，且报错很含糊（`request:fail`）。所以 `--dry-run` 这一步必须做一次，别等三个月后才发现续期脚本坏了。

---

## 七、防火墙与安全加固

```bash
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
sudo ufw status verbose
```

SSH 加固（可选但建议）：

```bash
# /etc/ssh/sshd_config
# PermitRootLogin no
# PasswordAuthentication no    ← 改成密钥登录后再改这句，否则会被锁在外面
sudo systemctl restart ssh
```

数据库端口确认不对外：

```bash
ss -lntp | grep 3306         # 应显示 127.0.0.1:3306，不是 0.0.0.0:3306
```

限制 journald 占用，避免日志撑爆磁盘：

```bash
# /etc/systemd/journald.conf
# SystemMaxUse=500M
sudo systemctl restart systemd-journald
```

---

## 八、备份（cron + mysqldump）

创建 `/usr/local/bin/backup-restaurant.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail

BACKUP_DIR=/var/backups/restaurant
DATE=$(date +%F_%H%M)
KEEP_DAYS=30

mkdir -p "$BACKUP_DIR"

mysqldump --single-transaction --quick --routines --triggers \
  -uroot -p"$(cat /root/.my.cnf.pass)" restaurant \
  | gzip > "$BACKUP_DIR/restaurant_${DATE}.sql.gz"

# 上传目录一并备份（菜品图丢了很麻烦）
tar -czf "$BACKUP_DIR/uploads_${DATE}.tar.gz" -C /var/www/restaurant-api uploads

find "$BACKUP_DIR" -type f -mtime +$KEEP_DAYS -delete
```

```bash
sudo chmod 700 /usr/local/bin/backup-restaurant.sh
sudo crontab -e
# 加一行：每天凌晨 3 点
# 0 3 * * * /usr/local/bin/backup-restaurant.sh >> /var/log/restaurant-backup.log 2>&1
```

**`--single-transaction` 不能省**：它让 InnoDB 在一致性快照上导出，不锁表。营业中备份也不会卡住下单。

**上线前必须做一次恢复演练**，否则备份等于没有：

```bash
gunzip -c /var/backups/restaurant/restaurant_日期.sql.gz | mysql -uroot -p restaurant_restore_test
mysql -uroot -p restaurant_restore_test -e "SELECT COUNT(*) FROM orders;"
```

---

## 九、部署验收清单

逐条打勾，缺一条都不算部署完成：

- [ ] `timedatectl` 显示 `Asia/Shanghai (CST, +0800)`
- [ ] `timedatectl timesync-status` 的 Offset 在 100ms 内
- [ ] `ss -lntp | grep 3306` 只有 `127.0.0.1:3306`
- [ ] `ss -lntp | grep 5000` 只有 `127.0.0.1:5000`
- [ ] `systemctl status restaurant-api` 是 `active (running)`
- [ ] `curl http://127.0.0.1:5000/api/ping` 返回 200
- [ ] `curl https://你的域名/api/ping` 返回 200（说明 Nginx 与证书正常）
- [ ] **WebSocket 握手成功**：`curl -i -N -H "Connection: Upgrade" -H "Upgrade: websocket" -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" https://你的域名/ws` 返回 **101**
- [ ] 写一条测试订单，`SELECT created_at FROM orders` 与当前北京时间**一致**（差 8 小时说明时区没配好）
- [ ] 上传一张菜品图，能从 `https://你的域名/uploads/xxx.jpg` 打开
- [ ] `certbot renew --dry-run` 通过
- [ ] 手动 `systemctl restart` 后，管理端 WebSocket **8 秒内自动重连**
- [ ] 备份脚本手动跑一次，能生成文件；再恢复到测试库能查到数据
- [ ] 小程序后台「服务器域名」已配 `https://` 与 `wss://` 两个合法域名

---

## 附录 A：常见错误对照表

| 现象 | 最可能的原因 | 排查与处理 |
|---|---|---|
| 502 Bad Gateway | 应用没起来，或端口不对 | `systemctl status restaurant-api`、`journalctl -u restaurant-api -n 50` |
| 应用启动即退出，日志有 `Couldn't find a valid ICU package` | 缺 `libicu` | `sudo apt install -y libicu74` |
| 应用启动失败 `Address already in use` | 5000 被占 | `ss -lntp \| grep 5000`，改 `ASPNETCORE_URLS` 或杀掉占位进程 |
| **订单时间差 8 小时** | 时区没设 | `timedatectl` 确认；systemd 里补 `Environment=TZ=Asia/Shanghai` |
| `/ws` 握手 400 / 426 | Nginx 缺 `Upgrade`/`Connection` 头 | 按 6.1 的 `/ws` 段补齐 |
| WebSocket 每 60 秒断一次 | `proxy_read_timeout` 用了默认值 | `/ws` 段设 `proxy_read_timeout 3600s` |
| 上传图片返回 413 | `client_max_body_size` 太小 | 已设 `10m`；同时检查 Kestrel `MaxRequestBodySize` |
| 菜品图 404 | 权限或路径 | `ls -l /var/www/restaurant-api/uploads`，属主必须是 `restaurant` |
| 微信支付签名报错 | 服务器时间偏差 > 5 分钟 | `timedatectl timesync-status`，必要时 `systemctl restart systemd-timesyncd` |
| 支付回调 404 | 小程序后台没配 `https://` 域名，或 Nginx 没转发到 `/api/pay/callback` | 看 Nginx access log 有没有这条请求 |
| 中文乱码 | 连接串没指定字符集，或库不是 utf8mb4 | 连接串加 `CharacterSet=utf8mb4`；`SHOW CREATE DATABASE restaurant` |
| `Access denied for user 'rest_app'` | 密码错，或授权 host 不匹配 | 授权用 `'rest_app'@'127.0.0.1'`；连接串写 `Server=127.0.0.1` 而不是 `localhost`（后者可能走 socket） |
| `systemctl restart` 很慢 | `TimeoutStopSec` 太长，或后台任务不响应取消 | 确认任务用 `PeriodicTimer` + `ct` 取消 |
| 磁盘突然满了 | journald / MySQL binlog / Serilog 日志 | `du -sh /var/log/* /var/lib/mysql`；配 `SystemMaxUse` 与 binlog 过期 |

---

## 附录 B：日常运维命令

```bash
# 服务
sudo systemctl status restaurant-api
sudo systemctl restart restaurant-api
sudo journalctl -u restaurant-api -f
sudo journalctl -u restaurant-api --since "1 hour ago" -p warning

# Nginx
sudo nginx -t
sudo systemctl reload nginx
sudo tail -f /var/log/nginx/access.log
sudo tail -f /var/log/nginx/error.log

# MySQL
sudo systemctl status mysql
mysql -uroot -p -e "SHOW PROCESSLIST;"
mysql -uroot -p -e "SHOW GLOBAL STATUS LIKE 'Threads_connected';"
sudo tail -f /var/log/mysql/slow.log

# 资源
df -h
du -sh /var/log/* /var/lib/mysql /var/www/restaurant-api/uploads
free -h
ss -lntp

# 证书
sudo certbot certificates
systemctl list-timers | grep certbot
```

---

## 附录 C：上线顺序（别跳步）

1. 域名备案通过 → A 记录指向服务器
2. 系统初始化（时区 / 时间同步 / ufw）
3. 装 MySQL 8.0 → 加固配置 → 建库 → 跑 DDL
4. 装 .NET 10 运行时 → 确认 `libicu` 在位
5. `dotnet publish` → 上传 → 设目录权限
6. 配置 systemd → 起服务 → `curl 127.0.0.1:5000/api/ping`
7. 装 Nginx → 配站点（先只开 80）→ `nginx -t`
8. certbot 申请证书 → 改 443 → 复查 `/ws` 的 101 握手
9. 小程序后台配 `https://` 与 `wss://` 合法域名
10. 跑完第九节验收清单
11. 配备份 cron → 做一次恢复演练
12. 提交审核 → 发布

**第 1 步和第 9 步是最容易卡住的行政环节，早点启动。**
