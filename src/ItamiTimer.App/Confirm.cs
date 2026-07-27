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

        var yes = new Button
        {
            Content = "是",
            Width = 96,
            Height = 34,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new Avalonia.CornerRadius(6),
        };
        var no = new Button
        {
            Content = "否",
            Width = 96,
            Height = 34,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new Avalonia.CornerRadius(6),
            IsDefault = true,
        };

        var dlg = new Window
        {
            Title = "一袋米要扛几楼",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
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
        dlg.Opened += (_, _) => DisableMinimize(dlg);

        await dlg.ShowDialog(owner);
        return result;
    }

    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [SupportedOSPlatform("windows")]
    private static void DisableMinimize(Window w)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (w.TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return;
        SetWindowLong(h, GWL_STYLE, GetWindowLong(h, GWL_STYLE) & ~WS_MINIMIZEBOX);
    }
}
