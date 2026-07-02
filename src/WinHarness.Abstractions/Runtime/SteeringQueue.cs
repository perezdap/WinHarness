using System.Collections.Concurrent;

namespace WinHarness.Runtime;

/// <summary>
/// Thread-safe queue of steering messages submitted while a turn is running.
/// The runtime drains pending messages between tool round-trips and injects
/// them as user messages within the same turn.
/// </summary>
public sealed class SteeringQueue
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>
    /// Gets the number of queued messages.
    /// </summary>
    public int Count => _messages.Count;

    /// <summary>
    /// Queues a steering message.
    /// </summary>
    public void Enqueue(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _messages.Enqueue(message);
    }

    /// <summary>
    /// Removes and returns all queued messages in FIFO order.
    /// </summary>
    public IReadOnlyList<string> DrainAll()
    {
        List<string> drained = [];
        while (_messages.TryDequeue(out string? message))
        {
            drained.Add(message);
        }

        return drained;
    }
}
