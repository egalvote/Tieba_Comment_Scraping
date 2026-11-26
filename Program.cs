using TiebaCrawler;

// 注册 GBK 等代码页编码支持（贴吧页面多为 GBK 编码）
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// ======== 启动项 ========
// 默认（无参数）      ：启动 Web API 抓取服务，通过 /replies 接口抓取帖子评论内容
// --browser           ：浏览器模式，默认无头=false，打开 https://tieba.baidu.com，
//                       进程保持存活，需用户手动关闭（Ctrl+C / 结束进程）才会退出
// =======================
var browserMode = args.Any(a =>
    a.Equals("--browser", StringComparison.OrdinalIgnoreCase) ||
    a.Equals("browser", StringComparison.OrdinalIgnoreCase));

if (browserMode)
{
    await BrowserMode.RunAsync(args);
    return;
}

// ---- 启动项 1：Web API 抓取服务（默认）----
var builder = WebApplication.CreateBuilder(args);

// 监听地址：默认仅本机，端口 5099
var listenUrl = Environment.GetEnvironmentVariable("TIEBA_LISTEN_URL") ?? "http://127.0.0.1:5099";
builder.WebHost.UseUrls(listenUrl);

builder.Services.AddSingleton<TiebaScraper>();

var app = builder.Build();

// 健康检查
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "tieba-content-crawler",
    time = DateTimeOffset.Now
}));

// 核心接口：抓取指定帖子「最新 N 条」回帖（倒序）
// GET /replies?url=https://tieba.baidu.com/p/11141961588&limit=50
app.MapGet("/replies", async (string url, int? limit, TiebaScraper scraper, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url) || !url.Contains("tieba.baidu.com"))
        return Results.BadRequest(new { error = "url 必须是贴吧帖子链接" });

    var take = limit ?? 50;
    if (take <= 0 || take > 200) take = 50;

    try
    {
        var (replies, totalPage) = await scraper.GetRepliesAsync(url, take, ct);
        return Results.Ok(new
        {
            url,
            count = replies.Count,
            totalPage,
            replies
        });
    }
    catch (TiebaBlockedException ex)
    {
        return Results.Json(new { error = "blocked", message = ex.Message }, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "failed", message = ex.Message }, statusCode: 500);
    }
});

app.Logger.LogInformation("贴吧评论内容爬取服务监听地址: {Url}", listenUrl);
app.Run();
