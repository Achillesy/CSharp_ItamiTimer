using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// 一个最小的「是 / 否」确认框。
///
/// Avalonia 没有内置 MessageBox，而这里只需要问一句话，不值得为它引入一个库或者
/// 再加一对 axaml。整个窗口在代码里搭出来，二十行。
///
/// 用在 §9 那一处：**专注中关窗口 = 放弃任务**。收起来该用最小化，两个动作长得像、
/// 后果差很远，所以必须问一次。
/// </summary>
public static class Confirm
{
    public static async Task<bool> AskAsync(Window owner, string message)
    {
        var result = false;

        // ⚠️ Padding=0 + VerticalContentAlignment=Center 必须一起给，否则文字顶着
        // 上边 —— 跟 MainWindow.axaml 的 Button.start 是同一个坑，理由见那里的注释。
        var yes = MakeButton("Yes");
        var no = MakeButton("No");
        no.IsDefault = true;

        var dlg = new Window
        {
            Title = "一袋米要扛几楼",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // 主窗口可能被图钉钉住了（§8.3.7），普通模态窗口会沉到它下面，
            // 表现成"点了 × 什么都没发生"。模态本来就该压在一切之上。
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0xD7, 0xDB, 0xE0)),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20, 18, 20, 16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { yes, no },
                    },
                },
            },
        };

        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => { result = false; dlg.Close(); };

        // 确认框上的最小化按钮是没有意义的（它是模态的，缩起来只会让人找不到），
        // 所以把它禁掉。Avalonia 没有对应属性，清掉 WS_MINIMIZEBOX 即可 ——
        // 系统会把按钮画成灰的，而不是整个抹掉，正是"disable"。
        //
        // **macOS 上不用做**：那边的模态窗口本来就不给最小化按钮 —— 系统自己就把
        // 这件事办了，没有什么可禁的。
        dlg.Opened += (_, _) => { if (OperatingSystem.IsWindows()) DisableMinimize(dlg); };

        await dlg.ShowDialog(owner);
        return result;
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Width = 96,
        Height = 34,
        Padding = new Avalonia.Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new Avalonia.CornerRadius(6),
    };

    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [SupportedOSPlatform("windows")]
    private static void DisableMinimize(Window w)
    {
        if (w.TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return;
        SetWindowLong(h, GWL_STYLE, GetWindowLong(h, GWL_STYLE) & ~WS_MINIMIZEBOX);
    }
}
