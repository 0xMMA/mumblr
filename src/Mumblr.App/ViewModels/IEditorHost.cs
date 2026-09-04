using System;
using System.Threading.Tasks;

namespace Mumblr.App.ViewModels;

/// <summary>
/// The view model's handle on the AvaloniaEdit document. Keeping it behind an interface means the
/// insert marker logic and the lock rules live in the view model, not in code-behind.
/// </summary>
public interface IEditorHost
{
    /// <summary>
    /// Raised whenever the document changes, including while the user types. Without it the status
    /// bar's character count only ever follows text mumblr wrote itself - and Idle, where the user
    /// edits, is exactly where it would stand still.
    /// </summary>
    event Action? TextChanged;

    string Text { get; set; }

    int CaretOffset { get; set; }

    bool IsReadOnly { get; set; }

    void Insert(int offset, string text);

    Task<bool> CopyToClipboardAsync(string text);
}
