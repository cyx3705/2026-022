using AppShell.Core.Commands;

namespace WBall.Presentation;

/// <summary>由应用装配层统一连接命令总线的业务视图。</summary>
internal interface ICommandBusAware
{
    void AttachBus(CommandBus bus);
}
