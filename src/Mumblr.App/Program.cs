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
    internal static string ResolveTargetDirectory(string[] args)
    {
        // Only the first argument is the target folder. Anything that starts with a dash belongs to
        // a switch, and its value must not be mistaken for a path.
        var candidate = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
        if (string.IsNullOrWhiteSpace(candidate))
            return Directory.GetCurrentDirectory();

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception)
        {
            return Directory.GetCurrentDirectory();
        }
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
