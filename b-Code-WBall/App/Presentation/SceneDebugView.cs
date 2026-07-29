using System.Windows;
using System.Windows.Controls;
using AppShell.Core.Commands;
using WBall.BallUi;
using WBall.Debug;
using WBall.Game;
using WBall.Model;

namespace WBall.Presentation;

public sealed class SceneDebugView : UserControl, ICommandBusAware
{
    private readonly SceneWorld _world;
    private readonly ObjectDebugView _objectView;
    private readonly BallObjectView _ballView;
    private readonly RefereeView _refereeView;
    private readonly TabControl _tabs;
    private SelectionKind _lastSelection;
    private bool _selectionRefreshQueued;

    public SceneDebugView(
        SceneWorld world,
        ObjectDebugView objectView,
        BallObjectView ballView,
        RefereeView refereeView)
    {
        _world = world;
        _objectView = objectView;
        _ballView = ballView;
        _refereeView = refereeView;
        MinWidth = 320;

        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "对象", Content = objectView },
                new TabItem { Header = "小球与公式", Content = ballView },
                new TabItem { Header = "裁判", Content = refereeView },
            },
        };
        Content = _tabs;

        _world.Changed += QueueSelectionRefresh;
        Loaded += (_, _) => RefreshSelection();
    }

    public int SelectedTabIndex => _tabs.SelectedIndex;

    public void AttachBus(CommandBus bus)
    {
        _objectView.AttachBus(bus);
        _ballView.AttachBus(bus);
        _refereeView.AttachBus(bus);
    }

    private void QueueSelectionRefresh()
    {
        if (_selectionRefreshQueued)
            return;
        _selectionRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _selectionRefreshQueued = false;
            RefreshSelection();
        });
    }

    private void RefreshSelection()
    {
        var selection = ResolveSelection();
        if (selection == _lastSelection)
            return;

        _lastSelection = selection;
        if (selection == SelectionKind.Ball)
            _tabs.SelectedIndex = 1;
        else if (selection == SelectionKind.Object)
            _tabs.SelectedIndex = 0;
    }

    private SelectionKind ResolveSelection()
    {
        if (_world.SelectedBallId is { } ballId && _world.FindBall(ballId) != null)
            return SelectionKind.Ball;
        if (_world.SelectedSolidId is { } solidId && _world.FindSolid(solidId) != null)
            return SelectionKind.Object;
        if (_world.SelectedId is { } objectId && _world.FindObject(objectId) != null)
            return SelectionKind.Object;
        return SelectionKind.None;
    }

    private enum SelectionKind
    {
        None,
        Object,
        Ball,
    }
}
