using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WBall.Battle;
using WBall.Presentation;
using WBall.Recording;
using WBall.Stage;

namespace WBall.Verify.Suites;

/// <summary>
/// v3.4 V34-09:出片页 suite（--render-page-smoke）。
/// 在 STA 线程上真实测量/布局「出片与时间」页,断言 300px 窄停靠下无横向溢出,
/// 并留一张视觉快照。唯一需要 WPF 视觉树的 suite,单独成文件。
/// </summary>
internal static class PageSuite
{
    public static int Run(VerifyRun run)
    {
        Exception? pageError = null;
        string? pagePath = null;
        var horizontalOverflow = double.NaN;

        var pageThread = new Thread(() =>
        {
            try
            {
                var pageRoot = run.Artifacts.Suite("page-smoke");
                var pageHarness = run.NewHarness(new BalanceConfig());
                var pageConfig = new RenderTimeConfigStore(Path.Combine(pageRoot, "config"), run.Log);
                var pageWorkspace = Path.Combine(pageRoot, "workspace");
                var pageScenarios = new ScenarioStore(pageWorkspace, run.Log);
                using var pageService = new RenderJobService(
                    pageHarness.EconomyWorld, pageHarness.BattleConfig, pageHarness.BalanceStore,
                    pageHarness.Weapons, new StageState(), pageScenarios, pageConfig,
                    run.Root, pageWorkspace, run.Log);
                var view = new RenderSettingsView(pageService) { Width = 300, Height = 720 };
                view.Measure(new Size(300, 720));
                view.Arrange(new Rect(0, 0, 300, 720));
                view.UpdateLayout();
                var scroll = VisualTreeProbe.FindVisualChild<System.Windows.Controls.ScrollViewer>(view)
                             ?? throw new InvalidOperationException("render page ScrollViewer missing");
                horizontalOverflow = scroll.ScrollableWidth;
                var bitmap = new RenderTargetBitmap(300, 720, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                pagePath = Path.Combine(pageRoot, "render-page-300x720.png");
                using var stream = File.Create(pagePath);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                pageError = ex;
            }
        });
        pageThread.SetApartmentState(ApartmentState.STA);
        pageThread.Start();
        pageThread.Join();

        run.Check("render page 300px layout has no horizontal overflow",
            pageError == null && horizontalOverflow < 0.5,
            pageError?.ToString() ?? $"overflow={horizontalOverflow:0.###}");
        run.Check("render page 300px visual snapshot is non-empty",
            pagePath != null && File.Exists(pagePath) && new FileInfo(pagePath).Length > 1_000,
            pagePath ?? pageError?.Message);
        if (pagePath != null)
            Console.WriteLine($"render page snapshot: {pagePath}");
        return run.Passed ? 0 : 1;
    }
}
