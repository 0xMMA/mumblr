using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mumblr.App.ViewModels;
using Mumblr.App.Views;
using Mumblr.Core.Config;
using Mumblr.Core.Stt;

namespace Mumblr.App.Tests;

/// <summary>
/// The real window against the real view model. The view model tests set properties directly;
/// these go through what the controls are bound to.
/// </summary>
public sealed class MainWindowTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), $"mumblr-window-{Guid.NewGuid():N}");
    private readonly FakeDeviceEnumerator devices = new();
    private readonly FakeCapture capture = new();
    private readonly FakeHotkeyService hotkeys = new();
    private readonly FakeSttEngineFactory engines = new();
    private readonly FakeClaudeRunner claude = new();
    private readonly FakeUpdateService updates = new();
    private readonly ConfigStore configStore;
    private MainWindow? window;
    private MainViewModel? viewModel;

    public MainWindowTests()
    {
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable(ApiKeyProvider.PrimaryVariable, "test-key");
        configStore = new ConfigStore(Path.Combine(workspace, "config.json"));
        var config = configStore.Load();
        config.MicrophoneDeviceId = "dev-1";
        configStore.Save(config);
    }

    private (MainWindow Window, MainViewModel ViewModel) Open()
    {
        window = new MainWindow();
        viewModel = new MainViewModel(workspace, window, configStore, devices, capture, hotkeys, claude, engines, updates);
        window.DataContext = viewModel;
        window.Show();
        viewModel.Initialize();
        return (window, viewModel);
    }

    private static async Task PumpAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_status_bar_switch_flips_the_hotkeys_through_its_command()
    {
        var (window, viewModel) = Open();
        var button = window.FindControl<Button>("HotkeySwitch")!;
        button.Content.ShouldBe("hotkeys: on");
        button.Classes.ShouldContain("on");

        button.Command!.Execute(null);

        viewModel.HotkeysEnabled.ShouldBeFalse();
        hotkeys.Stops.ShouldBe(1);
        button.Content.ShouldBe("hotkeys: off");
        button.Classes.ShouldContain("off");
        button.Classes.ShouldNotContain("on");

        button.Command.Execute(null);

        hotkeys.Started.ShouldNotBeNull();
        button.Content.ShouldBe("hotkeys: on");
    }

    [AvaloniaFact]
    public async Task The_switch_is_disabled_while_a_command_is_starting()
    {
        var (window, viewModel) = Open();
        var button = window.FindControl<Button>("HotkeySwitch")!;
        await viewModel.ToggleRecordingCommand.ExecuteAsync(null);
        var pause = new TaskCompletionSource();
        engines.Last!.StopGate = pause;

        hotkeys.PressCommandKey();
        await PumpAsync();

        // IsCommanding is still false here; the command's CanExecute is what the button follows.
        viewModel.IsCommanding.ShouldBeFalse();
        button.IsEffectivelyEnabled.ShouldBeFalse();

        pause.SetResult();
        hotkeys.ReleaseCommandKey();
        await PumpAsync();

        button.IsEffectivelyEnabled.ShouldBeTrue();
    }

    public void Dispose()
    {
        viewModel?.Shutdown();
        window?.Close();
        Environment.SetEnvironmentVariable(ApiKeyProvider.PrimaryVariable, null);

        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
    }
}
