using System.IO;

namespace WBall.Verify;

/// <summary>
/// v3.4 V34-02:验证器临时产物的生命周期。
///
/// 病根:旧实现 <c>Path.Combine(GetTempPath(), $"wball_verify_v32_{ProcessId}")</c> 只创建、从不删除,
/// 每跑一次留一个目录 —— 审计时本机已累积 50 个目录 / 5.8 GB。
///
/// 契约:
/// - 由 <c>using var</c> 声明持有(编译器生成 try/finally),正常返回与提前 return 都会走 <see cref="Dispose"/>;
/// - 通过则删除本次运行自建的目录;失败则保留并打印绝对路径;
/// - <c>--keep-artifacts</c> 强制保留,<c>--artifact-root &lt;path&gt;</c> 换根(长测/人工复盘);
/// - 清理异常只打 warning,不改变测试结论;
/// - 删除前二次校验目录名前缀,**只碰自己造的目录**,绝不递归系统临时目录的其它内容;
/// - 未捕获异常导致进程崩溃时 finally 不保证执行 —— 那种情况下产物天然被保留,正是我们想要的。
/// </summary>
internal sealed class VerifyArtifacts : IDisposable
{
    /// <summary>目录名前缀:既是可读标识,也是删除时的安全闸门。</summary>
    public const string Prefix = "wball_verify_v34_";

    private readonly Func<bool> _succeeded;
    private readonly bool _keep;
    private readonly bool _ownsRoot;
    private bool _disposed;

    private VerifyArtifacts(string root, bool keep, bool ownsRoot, Func<bool> succeeded)
    {
        Root = root;
        _keep = keep;
        _ownsRoot = ownsRoot;
        _succeeded = succeeded;
        Directory.CreateDirectory(Root);
    }

    /// <summary>本次运行的产物根目录(= 旧代码里的 dataRoot)。</summary>
    public string Root { get; }

    /// <param name="args">命令行参数,识别 --keep-artifacts / --artifact-root。</param>
    /// <param name="succeeded">收尾时求值的成功判据(通常是 failures.Count == 0)。</param>
    public static VerifyArtifacts Create(string[] args, Func<bool> succeeded)
    {
        var keep = args.Contains("--keep-artifacts", StringComparer.OrdinalIgnoreCase);
        var baseDir = ReadOption(args, "--artifact-root") ?? Path.GetTempPath();
        // 进程号不足以定位"哪一次跑的":加时间戳,长测翻目录时按时间排序即可
        var name = $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmss}_{Environment.ProcessId}";
        var root = Path.Combine(baseDir, name);
        return new VerifyArtifacts(root, keep, ownsRoot: true, succeeded);
    }

    /// <summary>每个 suite 一个子目录,互不干扰,失败时也便于定位是哪个 suite 留下的。</summary>
    public string Suite(string suite)
    {
        var path = Path.Combine(Root, suite);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (!_ownsRoot)
            return;

        if (_keep)
        {
            Console.WriteLine($"ARTIFACTS kept (--keep-artifacts): {Root}");
            return;
        }

        if (!_succeeded())
        {
            Console.WriteLine($"ARTIFACTS kept for diagnosis: {Root}");
            return;
        }

        // 安全闸门:只删自己造的、带前缀的目录
        var leaf = Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!leaf.StartsWith(Prefix, StringComparison.Ordinal))
        {
            Console.WriteLine($"WARN artifacts root not owned by verifier, left untouched: {Root}");
            return;
        }

        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            Console.WriteLine($"ARTIFACTS cleaned: {Root}");
        }
        catch (Exception ex)
        {
            // 清理失败不能污染测试结论:只报警
            Console.WriteLine($"WARN artifact cleanup failed ({ex.GetType().Name}: {ex.Message}); left at {Root}");
        }
    }

    private static string? ReadOption(string[] args, string option)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
