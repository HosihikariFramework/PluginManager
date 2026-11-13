namespace Hosihikari.Internal;

/// <summary>
/// 交给单个插件的宿主服务作用域。插件忘了退订的订阅、忘了释放的定时器，由它在卸载时一并收回。
/// 启用闸门（ADR-0035）：未启用或停用时，事件与定时器回调不再交给插件；订阅本身保留到卸载。
///
/// 不收回的后果是实测出来的：挂在一个<em>共享</em>总线上的 handler 会把插件的 ALC 钉死，
/// 卸载验证永远过不去 —— 而且订阅当时毫无异样，只有卸载时才暴露（ADR-0004）。
/// 框架知道是哪一次订阅属于哪个插件，就不该把这件事留给作者自觉。
/// </summary>
internal sealed class PluginScope(IEventBus events, IScheduler scheduler, PluginServiceRegistry registry)
    : IEventBus, IScheduler, IPluginServiceRegistry
{
    private readonly Lock _gate = new();
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
        // 单次任务完成后必须把自己从 _owned 摘掉；否则回调闭包会一直留到卸载。
        // 零延时可能在 Track 返回前就执行，所以完成与登记用同一把锁收口。
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

    /// <summary>打开闸门：之后到达的事件与定时器回调才会交给插件（ADR-0035）。</summary>
    internal void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            _enabled = true;
        }
    }

    /// <summary>关闭闸门；返回关闭前的状态，拒绝停用时据此恢复。</summary>
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

        // Dispose outside the lock: a callback being cancelled may use this scope.
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

    private sealed class Handle(IDisposable inner, PluginScope owner) : IDisposable
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
