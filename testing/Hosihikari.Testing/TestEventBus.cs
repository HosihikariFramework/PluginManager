using Hosihikari;

namespace Hosihikari.Testing;

/// <summary>同步事件总线，handler 异常被捕获到 <see cref="HandlerErrors"/>，不中断其余 handler。</summary>
public sealed class TestEventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];
    private readonly List<Exception> _errors = [];

    public IReadOnlyList<Exception> HandlerErrors => _errors;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, EventPriority priority = EventPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
        {
            _handlers[typeof(TEvent)] = list = [];
        }

        list.Add(handler);
        return new Subscription(() =>
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var current))
            {
                current.Remove(handler);
            }
        });
    }

    public void Publish<TEvent>(TEvent @event)
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
        {
            return;
        }

        foreach (var handler in list.ToArray())
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
