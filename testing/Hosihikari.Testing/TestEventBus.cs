using Hosihikari;

namespace Hosihikari.Testing;

/// <summary>
/// 同步事件总线，handler 异常被捕获到 <see cref="HandlerErrors"/>，不中断其余 handler。
/// 分档照生产实现（ADR-0060）：六桶、按档从低到高、同档按订阅顺序；越界档位在这里也抛。
/// </summary>
public sealed class TestEventBus : IEventBus
{
    private static readonly EventPriority[] Bands =
        [EventPriority.Lowest, EventPriority.Low, EventPriority.Normal, EventPriority.High, EventPriority.Highest, EventPriority.Monitor];

    private readonly Dictionary<Type, List<Delegate>[]> _handlers = [];
    private readonly List<Exception> _errors = [];

    public IReadOnlyList<Exception> HandlerErrors => _errors;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, EventPriority priority = EventPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var bucket = Array.IndexOf(Bands, priority);
        if (bucket < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown event priority");
        }

        if (!_handlers.TryGetValue(typeof(TEvent), out var buckets))
        {
            _handlers[typeof(TEvent)] = buckets = CreateBuckets();
        }

        buckets[bucket].Add(handler);
        return new Subscription(() =>
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var current))
            {
                current[bucket].Remove(handler);
                if (current.All(static list => list.Count == 0))
                {
                    _handlers.Remove(typeof(TEvent));
                }
            }
        });
    }

    public void Publish<TEvent>(TEvent @event)
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var buckets))
        {
            return;
        }

        // 快照整组桶，分发期间新增与退订都不影响这一轮。
        var snapshot = Array.ConvertAll(buckets, static list => list.ToArray());
        foreach (var band in snapshot)
        {
            foreach (var handler in band)
            {
                try
                {
                    ((Action<TEvent>)handler)(@event);
                }
                catch (Exception ex)
                {
                    _errors.Add(ex);
                }
            }
        }
    }

    private static List<Delegate>[] CreateBuckets() => [.. Bands.Select(static _ => new List<Delegate>())];

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            unsubscribe();
        }
    }
}
