namespace Mumblr.Core.Stt;

public enum SttMode
{
    /// <summary>One POST at stop. Highest accuracy, up to 1000 keyterms.</summary>
    Batch,

    /// <summary>WebSocket streaming. Committed segments append as you speak, 50 keyterms.</summary>
    Realtime,
}
