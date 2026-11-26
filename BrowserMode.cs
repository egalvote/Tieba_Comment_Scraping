using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace TiebaCrawler;

/// <summary>
/// 浏览器模式（第二个启动项）。
///
/// 与抓取服务（/replies 接口）不同，本模式不做任何抓取，只负责以
/// 「有头」方式打开 <c>https://tieba.baidu.com</c>，供用户手动登录 / 查看。
/// 进程会一直保持存活，需要用户手动关闭（Ctrl+C 或结束进程）才会退出。
/// </summary>
public static class BrowserMode
{
    public static async Task RunAsync(string[] args)
    {
        // 本启动项默认无头模式为 false（显示浏览器窗口）
        var headless = false;
        if (args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase)))
            headless = true;

        // 打开固定页面：贴吧首页
        const string url = "https://tieba.baidu.com";

        // 用户数据目录从项目根目录的 用户数据路径.txt 读取（与抓取逻辑共用）
        var userDataDir = Config.GetUserDataDir();
        var channel =
            Environment.GetEnvironmentVariable("TIEBA_CHANNEL") ?? "msedge";

        Console.WriteLine($"[浏览器模式] 用户数据目录: {userDataDir}");
        Console.WriteLine($"[浏览器模式] 无头模式: {headless}");
        Console.WriteLine($"[浏览器模式] 正在打开: {url}");

        var playwright = await Playwright.CreateAsync();
        var context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, new()
        {
            Channel = channel,
            Headless = headless,
            Args = new[] { "--no-sandbox", "--disable-blink-features=AutomationControlled" }
        });

        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

            Console.WriteLine("浏览器已打开，可手动登录 / 查看。");
            Console.WriteLine("按 Ctrl+C（或结束本进程）手动关闭...");

            // 保持进程存活，直到用户手动关闭（Ctrl+C / SIGTERM）
            using var cts = new CancellationTokenSource();
            using var _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => cts.Cancel());
            using var _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel());

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("收到退出信号，正在关闭浏览器...");
            }
        }
        finally
        {
            await context.DisposeAsync();
            playwright.Dispose();
        }
    }
}
