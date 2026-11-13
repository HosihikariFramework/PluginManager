using Hosihikari;

namespace Hosihikari.Testing;

/// <summary>
/// 测试版的作用域：镜像生产 <c>PluginScope</c> 的启用闸门、订阅/定时器回收与服务反登记。
/// 裸的 Test* 存储只用于观察；插件看到的 <see cref="IPluginContext"/> 走这个作用域。
/// </summary>
internal sealed class TestPluginScope(
    TestEventBus events,
    TestScheduler scheduler,
    TestServiceRegistry registry) : IEventBus, IScheduler, IPluginServiceRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<Handle> _owned = [];
    private readonly List<Type> _registered = [];
    private volatile bool _enabled;
    private bool _released;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, EventPriority priority = EventPriority.Normal) =>
        Track(() => events.Subscribe<TEvent>(@event =>
        {
            if (_enabled)
            {
                handler(@event);
            }
        }, priority));

    public void Publish<TEvent>(TEvent @event) => events.Publish(@event);

    public IDisposable ScheduleOnce(TimeSpan delay, Action work)
    {
        var gate = new object();
        var completed = false;
        Handle? handle = null;

        var tracked = Track(() => scheduler.ScheduleOnce(delay, () =>
        {
            try
            {
                if (_enabled)
                {
                    work();
                }
            }
            finally
            {
                lock (gate)
                {
                    completed = true;
                    handle?.Dispose();
                    handle = null;
                }
            }
        }));

        lock (gate)
        {
            handle = tracked;
            if (completed)
            {
                tracked.Dispose();
                handle = null;
            }
        }

        return tracked;
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action work) =>
        Track(() => scheduler.SchedulePeriodic(interval, () =>
        {
            if (_enabled)
            {
                work();
            }
        }));

    public bool Register<T>(T service) where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (!registry.Register(service))
            {
                return false;
            }

            _registered.Add(typeof(T));
            return true;
        }
    }

    public T? Get<T>() where T : class => registry.Get<T>();

    internal void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            _enabled = true;
        }
    }

    internal bool Close()
    {
        lock (_gate)
        {
            var wasEnabled = _enabled;
            _enabled = false;
            return wasEnabled;
        }
    }

    internal void ReleaseAll()
    {
        Handle[] owned;
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            _enabled = false;
            owned = [.. _owned];
            _owned.Clear();
            foreach (var type in _registered)
            {
                registry.Remove(type);
            }

            _registered.Clear();
        }

        foreach (var handle in owned)
        {
            handle.Dispose();
        }
    }

    private Handle Track(Func<IDisposable> create)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            var handle = new Handle(create(), this);
            _owned.Add(handle);
            return handle;
        }
    }

    private sealed class Handle(IDisposable inner, TestPluginScope owner) : IDisposable
    {
        private IDisposable? _inner = inner;

        public void Dispose()
        {
            var disposable = Interlocked.Exchange(ref _inner, null);
            if (disposable is null)
            {
                return;
            }

            lock (owner._gate)
            {
                owner._owned.Remove(this);
            }

            disposable.Dispose();
        }
    }
}
