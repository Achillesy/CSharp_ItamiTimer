using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ItamiTimer.App;

/// <summary>
/// Where the program's own stuff lives.
///
/// Pulled out on its own so <see cref="Settings"/> (which needs to write settings.json)
/// doesn't have to depend on <see cref="Log"/> for the directory, or vice versa -- neither
/// class should have to know the other exists just to agree on a path. ⚠️ The original
/// reason written here was "Log is a no-op in Release", which **stopped being true the
/// same day it was written** (see <see cref="Log"/>'s own doc comment: both configurations
/// write, reversed later on 2026-07-28) -- this class just never got the memo.
/// </summary>
public static class AppData
{
    /// <summary>
    /// Where settings.json and the log live.
    ///
    /// <code>
    /// Windows   %LOCALAPPDATA%\ItamiTimer
    /// macOS     ~/Library/Application Support/ItamiTimer
    /// </code>
    ///
    /// **Can't use <c>SpecialFolder.LocalApplicationData</c> directly on macOS**: .NET maps
    /// it to XDG's <c>~/.local/share</c> on Unix-like systems, a hidden directory Finder
    /// won't show at all. When a user wants to edit their own rules.json, they'd first need
    /// to know ⇧⌘. -- this program's config is meant to be hand-edited by the user in the
    /// first place (§8.1's chain is read-only), so hiding it would block that path entirely.
    /// </summary>
    public static string Dir { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       "Library", "Application Support", "ItamiTimer")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "ItamiTimer");

    /// <summary>
    /// The rules file is looked up in three tiers, **never just the current working
    /// directory**.
    ///
    /// <code>
    /// 1. %LOCALAPPDATA%\ItamiTimer\rules.json   <- the user's own, next to settings.json
    /// 2. &lt;exe's directory&gt;\rules.json           <- the default shipped with the program
    /// 3. .\rules.json                            <- for running from the repo root during development
    /// </code>
    ///
    /// **Why tier 1 exists**: republishing overwrites the copy next to the exe, which
    /// would wipe out any goals the user added. Keeping their copy here means any number of
    /// republishes never touch it. Deleting it gently falls back to the default rules.
    ///
    /// **Doesn't prioritize the working directory**: a desktop shortcut's "start in" folder
    /// can be anything, so looking up by working directory would make the program work
    /// sometimes and not others. This trap only surfaced on 2026-07-28 when building the
    /// Release shortcut -- every test up to that point happened to launch from the repo
    /// root, so the working directory always happened to be right.
    ///
    /// **This tier chain is read-only**, the program never lays down tier 1's file itself (§8.1).
    /// </summary>
    public static string RulesPath()
    {
        var mine = Path.Combine(Dir, "rules.json");
        if (File.Exists(mine)) return mine;

        var beside = Path.Combine(AppContext.BaseDirectory, "rules.json");
        return File.Exists(beside) ? beside : "rules.json";
    }

    /// <summary>
    /// How **the program's own two files** (settings.json / during.json) get written.
    ///
    /// One options object, not two -- `Settings` and `During` used to each write their own
    /// identical copy (even the comments matched), which is exactly the seed of "the same
    /// thing written twice, change one and forget the other" (§15.4's `executeCommand` grew
    /// out of exactly that).
    ///
    /// Doesn't escape non-ASCII: these two files are meant **to be read by a human**, and
    /// goal names can be in any language -- the default encoder would turn a name into a
    /// string of `\uXXXX` escapes, making it unrecognizable if someone wants to manually
    /// reset an entry to zero.
    /// (`Unsafe` refers to not escaping for an HTML context; these two files are only ever
    /// read and written by the program itself, never embedded in a web page.)
    ///
    /// ⚠️ This **must not** be used to read or write `rules.json` -- that file is
    /// hand-written by the user, the program only reads it, and its own parsing settings
    /// (comments, trailing commas, case-insensitivity) live in `GroupRules`.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Opens <see cref="Dir"/> in the OS's file manager -- Settings' "Open Config Folder"
    /// button (2026-08-13). Same motivation as <see cref="Dir"/> deliberately not being
    /// hidden on macOS: this config is meant to be hand-edited (rules.json, and now
    /// during.json's reset-by-deleting-the-entry trick too), so getting to it should be one
    /// click, not "go find %LOCALAPPDATA% yourself".
    ///
    /// Windows opens straight through `explorer.exe`, macOS through `open` -- neither needs
    /// `UseShellExecute` or output capture, unlike <see cref="Command.LaunchDetached"/>:
    /// that one runs whatever line the user wrote in rules.json, this one always launches
    /// the same fixed, trusted executable against <see cref="Dir"/>. `ArgumentList` (not a
    /// hand-built `Arguments` string) sidesteps the Windows argv/CRT quoting question
    /// entirely -- the same class of bug <see cref="Command"/>'s comments call out by name,
    /// not worth re-earning here over one path that might contain spaces.
    ///
    /// Creates the directory first in case it somehow doesn't exist yet (a fresh install
    /// that hasn't saved anything) -- opening a missing folder just fails silently on both
    /// platforms, and an empty folder is a better answer than nothing visibly happening.
    /// </summary>
    public static void OpenInFileManager()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "explorer.exe");
            psi.ArgumentList.Add(Dir);
            Process.Start(psi);
        }
        catch (Exception e)
        {
            Log.Error("Failed to open the config folder", e);
        }
    }
}
