using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Mumblr.Core.Commands;

namespace Mumblr.App.ViewModels;

/// <summary>
/// One entry in the channel 2 log. Nothing here ever reaches the content file - the log exists so
/// the 5-30 s round trip through <c>claude -p</c> is visible at a glance.
/// </summary>
public sealed partial class CommandLogItem : ObservableObject
{
    [ObservableProperty]
    private string commandText = "(listening...)";

    [ObservableProperty]
    private string response = string.Empty;

    [ObservableProperty]
    private CommandStatus status = CommandStatus.Recording;

    [ObservableProperty]
    private string duration = string.Empty;

    /// <summary>Which model and effort answered, so a downgraded config is visible in the log.</summary>
    [ObservableProperty]
    private string engine = string.Empty;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public string Time => StartedAt.ToString("HH:mm:ss");

    public string StatusText => Status switch
    {
        CommandStatus.Recording => "recording command",
        CommandStatus.Transcribing => "transcribing",
        CommandStatus.Running => "claude is working",
        CommandStatus.Succeeded => "done",
        CommandStatus.Failed => "failed",
        CommandStatus.Reverted => "reverted",
        _ => Status.ToString(),
    };

    public bool IsBusy => Status is CommandStatus.Recording or CommandStatus.Transcribing or CommandStatus.Running;

    public bool IsFailed => Status is CommandStatus.Failed;

    partial void OnStatusChanged(CommandStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsFailed));
    }
}
