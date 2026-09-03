namespace Mumblr.Core.Documents;

/// <summary>
/// Keeps the file contents from before each <c>claude -p</c> call so a bad command can be undone.
/// </summary>
public sealed class SnapshotStore
{
    private readonly Stack<Snapshot> snapshots = new();
    private readonly int capacity;

    public SnapshotStore(int capacity = 20) => this.capacity = capacity;

    public int Count => snapshots.Count;

    public bool CanRevert => snapshots.Count > 0;

    public void Push(string content, string label)
    {
        snapshots.Push(new Snapshot(content, label, DateTimeOffset.Now));

        if (snapshots.Count <= capacity)
            return;

        // Drop the oldest by rebuilding; the stack stays small so this is cheap.
        var kept = snapshots.Take(capacity).Reverse().ToList();
        snapshots.Clear();
        foreach (var snapshot in kept)
            snapshots.Push(snapshot);
    }

    public bool TryPop(out Snapshot snapshot)
    {
        if (snapshots.Count == 0)
        {
            snapshot = default;
            return false;
        }

        snapshot = snapshots.Pop();
        return true;
    }

    public readonly record struct Snapshot(string Content, string Label, DateTimeOffset TakenAt);
}
