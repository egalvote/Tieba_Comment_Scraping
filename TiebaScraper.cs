using Microsoft.Playwright;
using System.Text.Json;

namespace TiebaCrawler;

/// <summary>回帖 DTO（字段名与 Java 端 crawler/TiebaReply 对齐）</summary>
public sealed class TiebaReplyDto
{
    public string Content { get; set; } = "";
    public string TiebaId { get; set; } = "";
    public int? Level { get; set; }
    public long Floor { get; set; }
    public string Time { get; set; } = "";
}

/// <summary>被百度安全验证拦截</summary>
public sealed class TiebaBlockedException : Exception
{
    public TiebaBlockedException(string message) : base(message) { }
}

/// <summary>
/// 贴吧评论内容抓取器：复用 Playwright 持久化用户目录（已登录态）抓取贴吧帖子的「最新回复」。
///
/// 策略（实测 3466 楼大帖约 13 秒/50 楼）：
///   1. 打开 ?pn=1
///   2. 点击「倒序」，让列表按楼层从新到旧排列
///   3. 拦截 /c/f/pb/page_pc 的 JSON 响应（含 post_list + user_list.level_id）
///   4. 滚动 .pc-pb-box 触发增量加载，直到凑够 limit 或不再增长
///   5. 直接解析 JSON，不解析 DOM，不做全量扫描
/// </summary>
public sealed class TiebaScraper : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<TiebaScraper> _log;
    private IPlaywright? _playwright;
    private IBrowserContext? _context;

    // 用户数据目录从项目根目录的 用户数据路径.txt 读取
    private readonly string _userDataDir = Config.GetUserDataDir();

    private readonly string _channel =
        Environment.GetEnvironmentVariable("TIEBA_CHANNEL") ?? "msedge";

    private readonly bool _headless =
        !string.Equals(Environment.GetEnvironmentVariable("TIEBA_HEADLESS") ?? "true",
                       "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>最多滚动次数（每次约 1.2 秒，约 15 楼）</summary>
    private readonly int _maxScroll =
        int.TryParse(Environment.GetEnvironmentVariable("TIEBA_MAX_SCROLL"), out var s) ? s : 12;

    /// <summary>滚动间隔毫秒</summary>
    private readonly int _scrollIntervalMs =
        int.TryParse(Environment.GetEnvironmentVariable("TIEBA_SCROLL_INTERVAL_MS"), out var m) ? m : 1200;

    public TiebaScraper(ILogger<TiebaScraper> log) => _log = log;

    private async Task EnsureInitAsync()
    {
        if (_context != null) return;

        _log.LogInformation("初始化 Playwright: profile={Dir} headless={Headless} channel={Channel}",
            _userDataDir, _headless, _channel);

        _playwright ??= await Playwright.CreateAsync();
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, new()
        {
            Channel = _channel,
            Headless = _headless,
            Args = new[] { "--no-sandbox", "--disable-blink-features=AutomationControlled" }
        });
        _context.SetDefaultTimeout(60000);
    }

    /// <summary>抓取最新 limit 条回帖（倒序）</summary>
    public async Task<(List<TiebaReplyDto> Replies, int TotalPage)> GetRepliesAsync(
        string url, int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureInitAsync();
            var page = await _context!.NewPageAsync();
            try
            {
                var floors = new SortedDictionary<long, TiebaReplyDto>();
                var lockObj = new object();
                var totalPage = -1;
                var revSeen = 0;

                page.Response += async (_, resp) =>
                {
                    try
                    {
                        if (!resp.Url.Contains("page_pc") || resp.Status != 200) return;
                        var postData = resp.Request.PostData ?? "";
                        var body = await resp.TextAsync();
                        lock (lockObj)
                        {
                            Collect(body, postData, floors, ref totalPage, ref revSeen);
                        }
                    }
                    catch { /* 单条响应失败不影响整体 */ }
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();

                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                await page.WaitForTimeoutAsync(2000);

                var title = await page.TitleAsync();
                var bodyText = await page.Locator("body").InnerTextAsync();
                if (title.Contains("安全验证") || bodyText.Contains("百度安全验证"))
                    throw new TiebaBlockedException("百度安全验证拦截：登录态失效或当前 IP 被风控");

                // 点击「倒序」：楼层从新到旧
                var rev = page.GetByText("倒序", new() { Exact = true });
                var hasReverse = await rev.CountAsync() > 0;
                if (hasReverse)
                {
                    await rev.First.ClickAsync();
                    await page.WaitForTimeoutAsync(1800);
                }
                else
                {
                    _log.LogWarning("未找到「倒序」按钮，退化为正序单页: {Url}", url);
                }

                var lastCount = -1;
                var stagnant = 0;

                while (true)
                {
                    int count;
                    lock (lockObj) count = floors.Count;

                    if (count >= limit) break;
                    if (count == lastCount) stagnant++; else stagnant = 0;
                    lastCount = count;

                    // 正序帖（无倒序按钮）只取首屏，避免误判为“还有更多”
                    if (!hasReverse && stagnant >= 1) break;
                    if (stagnant >= 3) break;
                    if (sw.Elapsed.TotalSeconds > 60) break;

                    await page.EvaluateAsync(
                        "() => { const b = document.querySelector('.pc-pb-box'); if (b) b.scrollTop = b.scrollTop + 1400; }");
                    await page.WaitForTimeoutAsync(_scrollIntervalMs);
                }

                List<TiebaReplyDto> result;
                lock (lockObj)
                {
                    result = floors.Values
                        .OrderByDescending(x => x.Floor)
                        .Take(limit)
                        .ToList();
                }

                _log.LogInformation("抓取完成 url={Url} 楼层={Count} 总页数={Total} 耗时={Sec:F1}s",
                    url, result.Count, totalPage, sw.Elapsed.TotalSeconds);

                return (result, totalPage);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>从 page_pc 的 JSON 中提取楼层 + 作者等级</summary>
    private static void Collect(string body, string postData,
        SortedDictionary<long, TiebaReplyDto> floors, ref int totalPage, ref int revSeen)
    {
        if (postData.Contains("r=1")) revSeen++;
        if (revSeen == 0) return; // 只采倒序响应，避免混入正序首屏

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (totalPage < 0 && root.TryGetProperty("page", out var pg)
            && pg.TryGetProperty("total_page", out var tp)
            && tp.ValueKind == JsonValueKind.Number)
        {
            totalPage = tp.GetInt32();
        }

        // author_id -> (昵称, 等级)
        var users = new Dictionary<long, (string Name, int? Level)>();
        if (root.TryGetProperty("user_list", out var ul) && ul.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in ul.EnumerateArray())
            {
                if (!u.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                var id = idEl.GetInt64();
                var name = u.TryGetProperty("name_show", out var n1) && n1.ValueKind == JsonValueKind.String
                    ? n1.GetString()
                    : (u.TryGetProperty("name", out var n2) && n2.ValueKind == JsonValueKind.String ? n2.GetString() : null);
                int? lv = null;
                if (u.TryGetProperty("level_id", out var lvEl) && lvEl.ValueKind == JsonValueKind.Number)
                    lv = lvEl.GetInt32();
                users[id] = (name ?? "", lv);
            }
        }

        if (!root.TryGetProperty("post_list", out var pl) || pl.ValueKind != JsonValueKind.Array) return;

        foreach (var p in pl.EnumerateArray())
        {
            if (!p.TryGetProperty("floor", out var fEl) || fEl.ValueKind != JsonValueKind.Number) continue;
            var floor = fEl.GetInt64();
            var aid = p.TryGetProperty("author_id", out var aEl) && aEl.ValueKind == JsonValueKind.Number
                ? aEl.GetInt64() : 0;
            var text = ExtractText(p);

            (string Name, int? Level) user;
            if (!users.TryGetValue(aid, out user)) user = ("", null);
            var time = "";
            if (p.TryGetProperty("time", out var tEl) && tEl.ValueKind == JsonValueKind.Number)
                time = DateTimeOffset.FromUnixTimeSeconds(tEl.GetInt64()).ToString("yyyy-MM-dd HH:mm");

            if (!string.IsNullOrWhiteSpace(text))
            {
                floors[floor] = new TiebaReplyDto
                {
                    Content = text,
                    TiebaId = user.Name,
                    Level = user.Level,
                    Floor = floor,
                    Time = time
                };
            }

            // 楼中楼
            if (p.TryGetProperty("sub_post_list", out var subs) && subs.ValueKind == JsonValueKind.Array)
            {
                var subIdx = 0;
                foreach (var sp in subs.EnumerateArray())
                {
                    var sid = sp.TryGetProperty("author_id", out var sAid) && sAid.ValueKind == JsonValueKind.Number
                        ? sAid.GetInt64() : 0;
                    var stext = ExtractText(sp);
                    if (string.IsNullOrWhiteSpace(stext)) { subIdx++; continue; }
                    (string Name, int? Level) su;
                    if (!users.TryGetValue(sid, out su)) su = ("", null);
                    // 用负楼层避免与主楼层冲突
                    floors[-floor * 1000 - subIdx] = new TiebaReplyDto
                    {
                        Content = stext,
                        TiebaId = su.Name,
                        Level = su.Level,
                        Floor = floor,
                        Time = time
                    };
                    subIdx++;
                }
            }
        }
    }

    /// <summary>content 是 [{type:0,text:...}, {type:3,图片...}] 的数组</summary>
    private static string ExtractText(JsonElement post)
    {
        if (!post.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var seg in c.EnumerateArray())
        {
            if (seg.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
            }
        }
        return string.Join(" ", parts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null) await _context.DisposeAsync();
        _playwright?.Dispose();
    }
}
