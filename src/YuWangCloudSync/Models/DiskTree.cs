namespace YuWangCloudSync.Models;

/// <summary>
/// 育网云磁盘树节点（动态拉取后入 SQLite 的 disk_tree 表）。
///
/// 账号下是"容器(typeid) → 磁盘(diskid)"两级结构：
///   班级共享(typeid=100)
///     ├─ 24腾飞班级(typeid=101) → TF2428(diskid=2540)
///     ├─ 年级共享(typeid=106)   → 24腾飞年级(diskid=2555)
///     └─ 校级共享(typeid=102)   → 资料共享(2545)/语文(2558)/…/计算机(2568)
///
/// RootContainerTypeId=0 时，frame_desk 中的根节点作为容器，再递归列举其子节点；
/// 正数时，从指定服务端容器开始递归列举。
/// </summary>
public sealed class DiskTreeNode
{
    private static readonly char[] InvalidLocalNameChars = Path.GetInvalidFileNameChars();

    /// <summary>节点 id：容器为 typeid，磁盘为 diskid（与 NodeType 配套解释）</summary>
    public ulong Id { get; init; }
    /// <summary>"container" 或 "disk"</summary>
    public string NodeType { get; init; } = "";
    /// <summary>显示名（解析失败时为 id 字符串）</summary>
    public string Name { get; init; } = "";
    /// <summary>本地目录名；根页显示名中的盘符冒号等 Windows 非法字符映射为下划线。</summary>
    public string LocalName
    {
        get
        {
            var name = new string(Name.Select(c => Array.IndexOf(InvalidLocalNameChars, c) >= 0 ? '_' : c).ToArray())
                .TrimEnd(' ', '.');
            return name.Length == 0 ? Id.ToString() : name;
        }
    }
    /// <summary>父容器映射值；挂载根下节点的父值为 RootContainerTypeId</summary>
    public ulong ParentId { get; init; }

    public bool IsContainer => NodeType == "container";
    public bool IsDisk => NodeType == "disk";
}
