using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// A minimal "yes / no" confirmation dialog.
///
/// Avalonia has no built-in MessageBox, and all that's needed here is asking one
/// question, not worth pulling in a library or adding another axaml file for it. The
/// whole window is built in code, twenty lines.
///
/// Used at the one spot in §9: **closing the window mid-focus = abandoning the task**.
/// Minimizing should be the way to tuck it away, but the two actions look alike and lead
/// to very different consequences, so this has to ask once.
/// </summary>
public static class Confirm
{
    public static async Task<bool> AskAsync(Window owner, string message)
    {
        var result = false;

        // ⚠️ Padding=0 and VerticalContentAlignment=Center must be given together,
        // otherwise the text sits pinned to the top edge -- the same trap as
        // MainWindow.axaml's Button.start, see the reasoning there.
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
            // The main window might be pinned (§8.3.7), and an ordinary modal window would
            // sink beneath it, showing up as "I clicked X and nothing happened". A modal
            // should always sit above everything else.
            Topmost = true,
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

        // 窗口底色跟 Settings 窗口共用同一个键，按主题两套（v3.0.0，DESIGN §8.8）。
        // 这里原来是写死的 `#D7DBE0`，跟 SettingsWindow.axaml 里那个值**各写各的**；
        // 现在两处引同一个键，改一个地方两扇窗口一起动。
        // 用 DynamicResource 而不是当场 TryFindResource 取一次值：这扇框是模态的、
        // 活不过一次问答，理论上等不到主题切换——但绑定这条路不需要谁记得"切主题时
        // 也去刷一下确认框"，少一个将来会忘的前提。
        dlg[!Window.BackgroundProperty] = new DynamicResourceExtension("ItamiWindowBackground");

        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => { result = false; dlg.Close(); };

        // A minimize button on a confirmation dialog is meaningless (it's modal, and
        // minimizing it would just make it impossible to find), so it's disabled. Avalonia
        // has no property for this, so clearing WS_MINIMIZEBOX does it -- the system draws
        // the button greyed-out rather than removing it entirely, which is exactly "disable".
        //
        // **Not needed on macOS**: a modal window there doesn't get a minimize button to
        // begin with -- the system already handles it, nothing to disable.
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
