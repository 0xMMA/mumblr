namespace Mumblr.App.Attention;

/// <summary>For hosts that are not a window.</summary>
internal sealed class NullAttention : IAttentionService
{
    public void Begin()
    {
    }

    public void End()
    {
    }
}
