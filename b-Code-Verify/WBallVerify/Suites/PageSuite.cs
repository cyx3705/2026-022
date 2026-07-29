using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WBall.BallUi;
using WBall.Battle;
using WBall.Debug;
using WBall.Game;
using WBall.Model;
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
        string? sceneDebugPath = null;
        var sceneDebugOverflow = double.NaN;
        var ballTab = -1;
        var stableTab = -1;
        var objectTab = -1;

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

                var sceneWorld = new SceneWorld();
                var sceneDebug = new SceneDebugView(
                    sceneWorld,
                    new ObjectDebugView(sceneWorld),
                    new BallObjectView(sceneWorld),
                    new RefereeView(sceneWorld))
                {
                    Width = 320,
                    Height = 720,
                };
                var tabs = (TabControl)sceneDebug.Content;
                var overflows = new List<double>();
                for (var tabIndex = 0; tabIndex < tabs.Items.Count; tabIndex++)
                {
                    tabs.SelectedIndex = tabIndex;
                    sceneDebug.Measure(new Size(320, 720));
                    sceneDebug.Arrange(new Rect(0, 0, 320, 720));
                    sceneDebug.UpdateLayout();
                    overflows.AddRange(VisualTreeProbe.FindVisualChildren<ScrollViewer>(sceneDebug)
                        .Select(x => x.ScrollableWidth));
                }
                sceneDebugOverflow = overflows.Count == 0 ? 0 : overflows.Max();

                var ball = new Ball { Id = "page-ball", Color = "#FFFFFF" };
                sceneWorld.Balls.Add(ball);
                sceneWorld.SelectedBallId = ball.Id;
                sceneWorld.NotifyChanged(markDirty: false);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                ballTab = sceneDebug.SelectedTabIndex;

                tabs.SelectedIndex = 2;
                ball.Color = "#FF0000";
                sceneWorld.NotifyChanged(markDirty: false);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                stableTab = sceneDebug.SelectedTabIndex;

                sceneWorld.SelectedBallId = null;
                var sceneObject = new SceneObject { Id = "page-object", Type = SceneObjectType.Block };
                sceneWorld.Objects.Add(sceneObject);
                sceneWorld.SelectedId = sceneObject.Id;
                sceneWorld.NotifyChanged(markDirty: false);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                objectTab = sceneDebug.SelectedTabIndex;

                var sceneBitmap = new RenderTargetBitmap(320, 720, 96, 96, PixelFormats.Pbgra32);
                sceneBitmap.Render(sceneDebug);
                var sceneEncoder = new PngBitmapEncoder();
                sceneEncoder.Frames.Add(BitmapFrame.Create(sceneBitmap));
                sceneDebugPath = Path.Combine(pageRoot, "scene-debug-320x720.png");
                using var sceneStream = File.Create(sceneDebugPath);
                sceneEncoder.Save(sceneStream);
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
        run.Check("scene debug 320px layout has no horizontal overflow",
            pageError == null && sceneDebugOverflow < 0.5,
            pageError?.ToString() ?? $"overflow={sceneDebugOverflow:0.###}");
        run.Check("scene debug selection switches tabs without refresh stealing focus",
            ballTab == 1 && stableTab == 2 && objectTab == 0,
            $"ball={ballTab} stable={stableTab} object={objectTab}");
        run.Check("scene debug 320px visual snapshot is non-empty",
            sceneDebugPath != null && File.Exists(sceneDebugPath) && new FileInfo(sceneDebugPath).Length > 1_000,
            sceneDebugPath ?? pageError?.Message);
        if (pagePath != null)
            Console.WriteLine($"render page snapshot: {pagePath}");
        if (sceneDebugPath != null)
            Console.WriteLine($"scene debug snapshot: {sceneDebugPath}");
        return run.Passed ? 0 : 1;
    }
}
