using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hosihikari.Internal;

/// <summary>
/// 把托管的日志转发给父级的日志器（FX-0007 的出口一侧）。
///
/// 原生侧在启动时递进来一个 C 回调：<c>void (*)(void* user, int level, const char* utf8)</c>。
/// 等级由这里映射成整数，文本一律 UTF-8、以 null 结尾 —— 与这条缝上的其它字符串一致。
///
/// 回调存在静态槽里，而不是每个 logger 各存一份：Endstone reload 会换掉原生模块，
/// 但 Loader 仍要活着；新模块装上后更新这个槽，旧 logger 就重新指向新桥。
/// </summary>
internal sealed unsafe class NativeForwardingLoggerProvider : ILoggerProvider
{
    private static IntPtr s_callback;
    private static IntPtr s_user;

    /// <summary>更新父级日志出口；传 0 表示当前没有可用的桥，日志直接丢弃。</summary>
    internal static void SetSink(IntPtr callback, IntPtr user)
    {
        Volatile.Write(ref s_user, user);
        Volatile.Write(ref s_callback, callback);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? error,
            Func<TState, Exception?, string> formatter
        )
        {
            var callback = Volatile.Read(ref s_callback);
            if (!IsEnabled(logLevel) || callback == IntPtr.Zero)
            {
                return;
            }

            // 异常整段带上：在别人的进程里没有调试器，消息就是唯一的线索（FX-0009）。
            var message = $"{category}: {formatter(state, error)}";
            if (error is not null)
            {
                // 异步与迭代器的栈是编译器生成的（MoveNext、<Foo>d__3），不去谜读不懂。
                message += " —— " + error.Demystify();
            }

            var bytes = Encoding.UTF8.GetBytes(message + "\0");
            fixed (byte* text = bytes)
            {
                ((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, void>)callback)(
                    Volatile.Read(ref s_user),
                    Level(logLevel),
                    text
                );
            }
        }

        /// <summary>0 调试、1 信息、2 警告、3 错误、4 致命 —— 与两个载体的桥一致。</summary>
        private static int Level(LogLevel level) =>
            level switch
            {
                LogLevel.Trace or LogLevel.Debug => 0,
                LogLevel.Information => 1,
                LogLevel.Warning => 2,
                LogLevel.Error => 3,
                _ => 4,
            };
    }
}
