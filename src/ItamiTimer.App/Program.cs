using Avalonia;
using Avalonia.Headless;

namespace ItamiTimer.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The UI is silent toward the user, so **the reason for a crash matters even
        // more** -- otherwise the program just vanishes and nobody can say what happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Unhandled exception; the program is about to exit", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved exception in a background task", e.Exception);
            e.SetObserved();
        };

        try
        {
            // Debug exit: renders a few dial specimens off-screen, then exits.
            if (args is ["--dial-specimens", var outDir, ..])
            {
                HeadlessBuilder().SetupWithoutStarting();
                DialSpecimens.Render(outDir);
                return;
            }

            // Same idea: exports the tomato icon as a .ico embedded in the exe's
            // resources. The icon is still drawn in code, no bitmap in the repository --
            // that rule runs all the way from the dial down to here.
            if (args is ["--export-icon", var icoPath, ..])
            {
                HeadlessBuilder().SetupWithoutStarting();
                IconExport.Write(icoPath);
                return;
            }

            // Same idea, the macOS side: lays out a .iconset directory, handed off to
            // iconutil to compress into a .icns for the .app (see pack-macos.sh). Same
            // artwork, two different containers.
            if (args is ["--export-iconset", var iconsetDir, ..])
            {
                HeadlessBuilder().SetupWithoutStarting();
                IconExport.WriteIconset(iconsetDir);
                return;
            }

            // Single-instance limit (DESIGN §16.4): only blocks this normal startup path.
            // The debug exits are one-shot command-line tools that run once and exit, so
            // they don't conflict with "an instance already running" and don't need blocking.
            if (!SingleInstance.TryAcquire())
            {
                Log.Info("Another instance is already running; activated it and exiting.");
                return;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    /// <summary>
    /// The AppBuilder dedicated to the three debug exits. **The normal startup path
    /// doesn't go through here** -- it's still <see cref="BuildAvaloniaApp"/> +
    /// `UsePlatformDetect`.
    ///
    /// **Why they can't share one**: those three paths only draw onto a
    /// <c>RenderTargetBitmap</c> and don't need a windowing platform at all, while
    /// `UsePlatformDetect` goes and initializes the native windowing system. On a machine
    /// with a graphical session there's no visible difference, but **it crashes outright
    /// with no graphical session**:
    ///
    /// <code>
    /// Avalonia.Native was not able to start the RenderTimer   (-6661)
    /// </code>
    ///
    /// This means **the build step depends on a graphical session** -- `pack-macos.sh`'s
    /// `--export-iconset` call is exactly part of the build, and it would fall over on CI.
    /// A build shouldn't care whether anyone's logged into the desktop.
    ///
    /// <c>UseHeadlessDrawing = false</c> is the other key half: the headless platform's
    /// drawing is a no-op by default; turning that off and attaching <c>UseSkia</c> makes
    /// it render **real pixels**, the same Skia backend as a normal launch.
    ///
    /// Verification method (2026-07-28): the 11 artifacts from <c>--export-icon</c> and
    /// <c>--export-iconset</c> were **byte-for-byte SHA-256 identical** before and after
    /// this change, showing the rendering result was unaffected. The 8 dial specimens
    /// **can't be compared by checksum** -- they draw the hands at the current moment, so
    /// running the same build twice produces different hashes (verified in practice); that
    /// part was checked visually instead.
    /// </summary>
    private static AppBuilder HeadlessBuilder() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .LogToTrace();
}
