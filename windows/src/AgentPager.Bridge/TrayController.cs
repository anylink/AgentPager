using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed class TrayController : IDisposable
{
    private readonly BridgeRuntime _runtime;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _server;
    private readonly ToolStripMenuItem _hook;
    private readonly ToolStripMenuItem _link;
    private readonly Icon _ownedIcon;

    public TrayController(BridgeRuntime runtime, Action showWindow, Action exit)
    {
        _runtime = runtime;
        _ownedIcon = LoadIcon();
        _server = new() { Enabled = false };
        _hook = new() { Enabled = false };
        _link = new() { Enabled = false };
        var menu = new ContextMenuStrip();
        var agentMenuItems = BuildAgentMenuItems().ToArray();
        var items = new List<ToolStripItem>
        {
            _server, _hook, _link,
            new ToolStripSeparator(),
        };
        items.AddRange(agentMenuItems);
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("打开 AgentPager 设置", null, (_, _) => showWindow()));
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("退出 AgentPager", null, (_, _) => exit()));
        menu.Items.AddRange(items.ToArray());
        _icon = new()
        {
            Icon = _ownedIcon,
            Text = "AgentPager Bridge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => showWindow();
        Refresh();
    }

    /// <summary>
    /// 遍历 <see cref="AgentRegistry.All"/> 为每个 Agent Adapter 生成
    /// "安装或修复 XX Hook" 菜单项。新增 Agent 时无需修改本方法。
    /// PR2 起：TrayController 完全数据驱动，不再硬编码 agent 名称。
    /// </summary>
    private IEnumerable<ToolStripMenuItem> BuildAgentMenuItems()
    {
        foreach (var adapter in AgentRegistry.All)
        {
            var captured = adapter;
            var displayName = captured.Descriptor.DisplayName;
            yield return new ToolStripMenuItem(
                $"安装或修复 {displayName} Hook",
                null,
                (_, _) => _runtime.InstallHook(captured)
            );
        }
    }

    public void Refresh()
    {
        var state = _runtime.State;
        _server.Text = $"SERVER  {state.ServiceStatus}";
        _hook.Text = $"HOOK    {(state.HookInstalled ? "READY" : "OFF")}";
        _link.Text = $"LINK    {(state.PhoneCount > 0 ? $"{state.PhoneCount} PHONE" : "WAIT")}";
        _icon.Text = state.PhoneCount > 0 ? $"AgentPager · {state.PhoneCount} 台手机" : "AgentPager Bridge";
    }

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/agentpager-icon.png"))
            ?? throw new InvalidOperationException("缺少 AgentPager 图标资源。");
        using var bitmap = new Bitmap(resource.Stream);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
