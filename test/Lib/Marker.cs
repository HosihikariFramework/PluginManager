namespace Lib;

/// <summary>
/// 探针用的普通依赖：Foo 与 Bar 都引用它，它既不是契约、也不在共享层里。
/// 它说明的是那两条能力边界（FX-0026）：普通依赖按 ALC 各一份，插件之间不共享类型。
/// </summary>
public static class Marker;
