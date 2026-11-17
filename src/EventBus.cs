using Microsoft.Extensions.Logging;

namespace Hosihikari.Internal;

internal sealed class EventBus(ILogger logger) : IEventBus
{
    private static readonly EventPriority[] Bands =
        [EventPriority.Lowest, EventPriority.Low, EventPriority.Normal, EventPriority.High, EventPriority.Highest, EventPriority.Monitor];

    private readonly Lock _gate = new();
    // 每个事件类型六个桶，按 Bands 的次序分发（ADR-0060）；桶内按订阅顺序。
    private readonly Dictionary<Type, List<Delegate>[]> _handlers = [];

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, EventPriority priority = EventPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var bucket = Array.IndexOf(Bands, priority);
        if (bucket < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown event priority");
        }

        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var buckets))
            {
                _handlers[typeof(TEvent)] = buckets = CreateBuckets();
            }

            buckets[bucket].Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var buckets))
                {
                    var list = buckets[bucket];
                    list.Remove(handler);
                    if (buckets.All(static list => list.Count == 0))
                    {
                        _handlers.Remove(typeof(TEvent));
                    }
                }
            }
        });
    }

    private static List<Delegate>[] CreateBuckets() => [.. Bands.Select(static _ => new List<Delegate>())];

    public void Publish<TEvent>(TEvent @event)
    {
        Delegate[][] snapshot;
        lock (_gate)
        {
            // 快照整组桶：分发期间新订阅的进桶，退订的从桶里摘，都影响不到这一轮。
            snapshot = _handlers.TryGetValue(typeof(TEvent), out var buckets)
                ? [.. buckets.Select(static list => list.ToArray())]
                : [];
        }

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
                    // 隔离到这一个 handler，继续分发给其余 —— ADR-0009 在事件总线上的落地（ADR-0014）。
                    logger.LogError(ex, "{Message}", CoreText.Get("Event_HandlerThrew", typeof(TEvent).Name));
                }
            }
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}

internal sealed class Scheduler(ILogger logger) : IScheduler
{
    public IDisposable ScheduleOnce(TimeSpan delay, Action work)
    {
        // 先建成不触发的，赋值之后再 Change —— 否则回调可能早于赋值而拿不到自己。
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer!.Dispose();
            Fire(work);
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(delay, Timeout.InfiniteTimeSpan);
        return new Handle(timer);
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action work) =>
        new Handle(new Timer(_ => Fire(work), null, interval, interval));

    private void Fire(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            // 定时器里的抛出没有调用方可回传，只能落进可见通道（ADR-0009）。
            logger.LogError(ex, "{Message}", CoreText.Get("Event_ScheduledThrew"));
        }
    }

    private sealed class Handle(Timer timer) : IDisposable
    {
        public void Dispose() => timer.Dispose();
    }
}
