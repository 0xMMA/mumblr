using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Mumblr.App.ViewModels;

namespace Mumblr.App.Views;

/// <summary>
/// Hosts the AvaloniaEdit buffer and exposes it to the view model through <see cref="IEditorHost"/>.
/// The lock and the insert marker are decided in the view model; this class only carries them out.
/// </summary>
public partial class MainWindow : Window, IEditorHost
{
    private TextEditor? editor;

    public MainWindow()
    {
        InitializeComponent();

        editor = this.FindControl<TextEditor>("Editor");
        if (editor is not null)
        {
            editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("MarkDown");
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string Text
    {
        get => editor?.Document?.Text ?? string.Empty;
        set
        {
            if (editor?.Document is null || editor.Document.Text == value)
                return;

            var wasReadOnly = editor.IsReadOnly;
            editor.IsReadOnly = false;
            editor.Document.Text = value;
            editor.IsReadOnly = wasReadOnly;
        }
    }

    public int CaretOffset
    {
        get => editor?.CaretOffset ?? 0;
        set
        {
            if (editor is not null)
                editor.CaretOffset = Math.Clamp(value, 0, Text.Length);
        }
    }

    public bool IsReadOnly
    {
        get => editor?.IsReadOnly ?? false;
        set
        {
            if (editor is not null)
                editor.IsReadOnly = value;
        }
    }

    public void Insert(int offset, string text)
    {
        if (editor?.Document is null || string.IsNullOrEmpty(text))
            return;

        // The document itself is never read-only, only the editor is, so committed segments can be
        // written while the user is locked out.
        var clamped = Math.Clamp(offset, 0, editor.Document.TextLength);
        editor.Document.Insert(clamped, text);

        var end = clamped + text.Length;
        editor.CaretOffset = end;
        editor.ScrollToLine(editor.Document.GetLineByOffset(end).LineNumber);
    }

    public async Task<bool> CopyToClipboardAsync(string text)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return false;

        await clipboard.SetTextAsync(text);
        return true;
    }
}
