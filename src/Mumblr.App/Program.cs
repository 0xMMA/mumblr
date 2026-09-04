using System;
using System.IO;
using Avalonia;
using Velopack;

namespace Mumblr.App;

internal static class Program
{
    /// <summary>The folder the dictation file is written to. Set from the command line before the UI starts.</summary>
    public static string TargetDirectory { get; private set; } = Directory.GetCurrentDirectory();

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack has to run first: it handles install/update/uninstall hooks and exits for them.
        VelopackApp.Build().Run();

        TargetDirectory = ResolveTargetDirectory(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>`mumblr .` means "write the dictation file here". No argument means the current directory.</summary>
    internal static string ResolveTargetDirectory(string[] args) =>
        ResolveTargetDirectory(args, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    internal static string ResolveTargetDirectory(string[] args, string currentDirectory, string applicationDirectory)
    {
        // Only the first argument is the target folder. Anything that starts with a dash belongs to
        // a switch, and its value must not be mistaken for a path.
        var candidate = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;

        string target;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            target = currentDirectory;
        }
        else
        {
            try
            {
                target = Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                target = currentDirectory;
            }
        }

        return IsInsideApplicationDirectory(target, applicationDirectory) ? FallbackDirectory() : target;
    }

    /// <summary>
    /// Launched from the start menu shortcut, the working directory is the install folder itself -
    /// and for a Velopack install that is `current`, which the next update replaces wholesale. A
    /// dictation written there is deleted by the first update that lands. Nothing about the app is
    /// meant to live inside its own program folder, so anywhere else is safer than there.
    /// </summary>
    internal static bool IsInsideApplicationDirectory(string target, string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
            return false;

        try
        {
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return candidate.Equals(app, comparison)
                   || candidate.StartsWith(app + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FallbackDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fallback = string.IsNullOrWhiteSpace(documents)
            ? Path.Combine(Path.GetTempPath(), "mumblr")
            : Path.Combine(documents, "mumblr");

        Directory.CreateDirectory(fallback);
        return fallback;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
