using System.Threading.Tasks;

namespace Mumblr.App.ViewModels;

/// <summary>
/// The view model's handle on the AvaloniaEdit document. Keeping it behind an interface means the
/// insert marker logic and the lock rules live in the view model, not in code-behind.
/// </summary>
public interface IEditorHost
{
    string Text { get; set; }

    int CaretOffset { get; set; }

    bool IsReadOnly { get; set; }

    void Insert(int offset, string text);

    Task<bool> CopyToClipboardAsync(string text);
}
