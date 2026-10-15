using SpencerDenim.Domain.Analytics;

namespace SpencerDenim.Application.Analytics;

/// <summary>
/// Hands a classified page view to the batched writer (doc 08 §3.8: "Batched insert from an in-memory
/// channel (every 5 s or 200 rows)").
/// <para>
/// <b>Never blocks, never throws, never touches the database.</b> It is on every page view, so a full queue
/// or a database that is down costs the view, not the page: <see cref="TryRecord"/> answers
/// <see langword="false"/> and the caller carries on.
/// </para>
/// </summary>
public interface IPageViewRecorder
{
    /// <summary>Queues <paramref name="view"/>; <see langword="false"/> when recording is off or the queue is full.</summary>
    bool TryRecord(PageView view);
}
