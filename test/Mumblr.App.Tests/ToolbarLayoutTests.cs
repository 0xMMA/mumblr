using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mumblr.App.ViewModels;
using Mumblr.App.Views;
using Mumblr.Core.Config;
using Mumblr.Core.Stt;

namespace Mumblr.App.Tests;

/// <summary>
/// The toolbar used to be a fixed column Grid, so the last control was clipped whenever the window
/// was narrower than the sum of its parts. It wraps now; these tests pin both ends of that.
/// </summary>
public sealed class ToolbarLayoutTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), $"mumblr-{Guid.NewGuid():N}");
    private MainViewModel? viewModel;
    private MainWindow? window;

    public ToolbarLayoutTests()
    {
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable(ApiKeyProvider.PrimaryVariable, "test-key");
    }

    private MainWindow Show(double? width = null)
    {
        window = new MainWindow();
        viewModel = new MainViewModel(
            workspace,
            window,
            new ConfigStore(Path.Combine(workspace, "config.json")),
            new FakeDeviceEnumerator(),
            new FakeCapture(),
            new FakeHotkeyService(),
            new FakeClaudeRunner(),
            new FakeSttEngineFactory());

        window.DataContext = viewModel;
        if (width is not null)
            window.Width = width.Value;

        window.Show();
        viewModel.Initialize();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static WrapPanel Toolbar(MainWindow window) =>
        window.GetVisualDescendants().OfType<WrapPanel>().First();

    private static IEnumerable<Control> Items(WrapPanel toolbar) =>
        toolbar.GetLogicalChildren().OfType<Control>();

    [AvaloniaFact]
    public void Every_control_stays_inside_the_toolbar_at_the_default_width()
    {
        var toolbar = Toolbar(Show());

        foreach (var item in Items(toolbar))
            item.Bounds.Right.ShouldBeLessThanOrEqualTo(toolbar.Bounds.Width + 0.5);
    }

    [AvaloniaFact]
    public void The_default_width_keeps_the_toolbar_on_one_line()
    {
        var toolbar = Toolbar(Show());

        var tallest = Items(toolbar).Max(item => item.Bounds.Height);

        // One line means the panel is no taller than its tallest item plus the shared margin.
        // There is roughly 140px of slack at this width, which is the headroom for font and DPI
        // differences between here and Windows.
        toolbar.Bounds.Height.ShouldBeLessThanOrEqualTo(tallest + 8);
    }

    [AvaloniaFact]
    public void A_narrow_window_wraps_instead_of_clipping()
    {
        var toolbar = Toolbar(Show(width: 720));

        var items = Items(toolbar).ToList();

        items.Count.ShouldBeGreaterThan(4);
        foreach (var item in items)
            item.Bounds.Right.ShouldBeLessThanOrEqualTo(toolbar.Bounds.Width + 0.5);

        // Everything no longer fits on one line, so the panel has to have grown.
        var tallest = items.Max(item => item.Bounds.Height);
        toolbar.Bounds.Height.ShouldBeGreaterThan(tallest + 8);
    }

    public void Dispose()
    {
        viewModel?.Shutdown();
        window?.Close();

        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
    }
}
