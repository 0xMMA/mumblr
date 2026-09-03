using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mumblr.App.ViewModels;
using Mumblr.App.Views;

namespace Mumblr.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainViewModel(Program.TargetDirectory, window);

            window.DataContext = viewModel;
            window.Opened += (_, _) => viewModel.Initialize();
            desktop.ShutdownRequested += (_, _) => viewModel.Shutdown();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
