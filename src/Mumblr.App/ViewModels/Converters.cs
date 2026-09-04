using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Mumblr.App.ViewModels;

/// <summary>Small value converters used by the main window.</summary>
public static class Converters
{
    /// <summary>Warnings turn the status line red, everything else stays dim.</summary>
    public static readonly IValueConverter WarningBrush = new FuncValueConverter<bool, IBrush>(
        isWarning => isWarning
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA2)));

    /// <summary>Green when a key is present, red when it is not. The key itself is never shown.</summary>
    public static readonly IValueConverter ApiBrush = new FuncValueConverter<bool, IBrush>(
        hasKey => hasKey
            ? new SolidColorBrush(Color.FromRgb(0x5A, 0xD1, 0x8B))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
}
