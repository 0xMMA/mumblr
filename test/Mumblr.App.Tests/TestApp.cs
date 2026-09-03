using Avalonia;
using Avalonia.Headless;
using Mumblr.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Mumblr.App.Tests;

/// <summary>Boots a headless Avalonia app so the dispatcher the view model posts to actually runs.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Mumblr.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
