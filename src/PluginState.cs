namespace Hosihikari;

/// <summary>插件在管理器表里的在场状态（ADR-0025）。启用与停用是另一条轴。</summary>
public enum PluginState
{
    /// <summary>实例与作用域在场；插件可以接活或处于停用。</summary>
    Loaded,

    /// <summary>已放下实例与作用域，等待搬运单元回收；仍在管理器表中。</summary>
    Detached,
}
