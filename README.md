# 贴吧评论内容爬取

基于 [.NET 10](https://dotnet.microsoft.com) + [Playwright](https://playwright.dev) 的贴吧帖子**评论内容**抓取工具。复用 Playwright 持久化用户目录（已登录态），通过拦截贴吧 `/c/f/pb/page_pc` 接口 JSON 抓取指定帖子的最新回帖（倒序），并对外提供 Web API。

适用于多平台（Windows/Linux）

---

## 爬取内容说明

`GET /replies` 返回的 JSON 结构如下：

| 字段 | 含义 |
| --- | --- |
| `url` | 请求的帖子链接 |
| `count` | 实际抓到的回帖条数 |
| `totalPage` | 帖子总页数（`-1` 表示未能获取） |
| `replies` | 回帖数组，**倒序**（最新楼层在前） |

`replies` 中每条回帖的字段：

| 字段 | 含义 |
| --- | --- |
| `content` | 评论内容（纯文本，图片等以占位符形式出现） |
| `tiebaId` | 用户昵称 |
| `level` | 用户等级（`level_id`） |
| `floor` | 楼层号 |
| `time` | 发布时间（`yyyy-MM-dd HH:mm`，本地时区） |

---
## 必要依赖
- Edge浏览器
## 路径配置

Playwright 持久化**用户数据目录**（含登录态）统一从项目根目录的 [用户数据路径.txt](用户数据路径.txt) 读取：

```
/home/user/桌面/playwright-userdata/    #示例
```
- 修改该文件即可切换用户数据目录（若路径下没有内容则会在第一次创建用户数据）；文件缺失或为空时会抛出明确异常。

## 两个启动项

### 首次启动登录用户状态：浏览器模式

以**有头**方式打开贴吧首页，供手动登录 / 查看，**不做任何抓取**。进程保持存活，需**手动关闭**（`Ctrl+C` 或结束进程）才退出。

```bash
dotnet run -- --browser
```

- 默认 `headless = false`（显示浏览器窗口），可加 `--headless` 改为无头。
- 固定打开 `https://tieba.baidu.com`。
- 用于维护登录态：在此页面完成登录后，抓取服务即可复用该登录态。

### 服务启动：Web API 抓取服务（默认）

不带参数运行，启动 HTTP 服务，通过接口抓取。

```bash
dotnet run
```

默认监听 `http://127.0.0.1:5099`（可用环境变量 `TIEBA_LISTEN_URL` 覆盖）。

| 接口 | 说明 |
| --- | --- |
| `GET /health` | 健康检查 |
| `GET /replies?url=<帖子链接>&limit=<条数>` | 抓取指定帖子最新 `limit` 条回帖（倒序）。`limit` 取值 1–200，默认 50；`url` 必须是 `tieba.baidu.com` 的帖子链接 |

示例：

```bash
curl "http://127.0.0.1:5099/replies?url=https://tieba.baidu.com/p/11231737377&limit=50"
```

返回码：`200` 成功；`400` 参数错误；`503` 被百度安全验证拦截（`blocked`）；`500` 其它失败。



---



### 其它环境变量（可选）

| 变量 | 默认 | 作用 |
| --- | --- | --- |
| `TIEBA_LISTEN_URL` | `http://127.0.0.1:5099` | 抓取服务监听地址 |
| `TIEBA_CHANNEL` | `msedge` | 浏览器通道（`msedge` / `chrome` 等） |
| `TIEBA_HEADLESS` | `true` | **仅抓取服务**：是否无头（浏览器模式默认有头，与此无关） |
| `TIEBA_MAX_SCROLL` | `12` | 最大滚动次数（每次约 1.2s、约 15 楼） |
| `TIEBA_SCROLL_INTERVAL_MS` | `1200` | 滚动间隔（毫秒） |

---

## ⚠️ 注意：不要多开

**同一时间只能运行一个实例**，请勿同时启动多个进程（尤其是多个占用同一用户数据目录的实例）。

原因：Chromium 的持久化用户数据目录（`user-data-dir`）**同一时间只允许被一个进程占用**。多开会导致：

- 新实例启动时**用户数据目录被占用**（`ProfileInUse` / 锁定）而启动失败，或
- 两个进程争抢同一份登录态，出现数据损坏 / 登录态错乱。

此外，多个 Web API 实例也无法绑定同一端口（`5099`），会报端口占用。

> 正确做法：结束上一个进程后再启动新的；浏览器模式与抓取服务若共用同一用户数据目录，也应避免同时运行。

---

## 许可证

本项目基于 **MIT** 许可证发布，完整条款见 [LICENSE](LICENSE)。



