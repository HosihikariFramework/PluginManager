using Microsoft.Extensions.Logging;

namespace Hosihikari.Internal;

internal sealed class EventBus(ILogger logger) : IEventBus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                _handlers[typeof(TEvent)] = list = [];
            }

            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                    {
                        _handlers.Remove(typeof(TEvent));
                    }
                }
            }
        });
    }

    public void Publish<TEvent>(TEvent @event)
    {
        Delegate[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers.TryGetValue(typeof(TEvent), out var list) ? [.. list] : [];
        }

        foreach (var handler in snapshot)
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
