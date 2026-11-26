namespace TiebaCrawler;

/// <summary>从项目根目录的配置文件读取路径。</summary>
public static class Config
{
    private const string UserDirFile = "用户数据路径.txt";

    /// <summary>
    /// 从项目根目录的 <c>用户数据路径.txt</c> 读取 Playwright 持久化用户数据目录
    /// （取第一行，自动去除首尾空白）。抓取逻辑与浏览器模式共用此路径。
    /// </summary>
    public static string GetUserDataDir()
    {
        var file = Path.Combine(Directory.GetCurrentDirectory(), UserDirFile);
        if (!File.Exists(file))
            throw new FileNotFoundException($"未找到用户数据路径文件: {file}，请创建该文件并写入用户数据目录路径");

        var dir = File.ReadAllLines(file)
                       .Select(l => l.Trim())
                       .FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"用户数据路径文件 {file} 中没有有效路径");

        return dir;
    }
}
