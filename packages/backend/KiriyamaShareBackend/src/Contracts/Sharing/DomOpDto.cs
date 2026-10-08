using System.Text.Json.Serialization;

namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 一条作用在**共同基线**上的 DOM 操作。与前端 <c>types/index.ts</c> 的 <c>DomOp</c> 对应。
///
/// 为什么是"操作"而不是"MutationRecord 的归一化"：
/// MutationRecord 描述的是"DOM 刚才发生了什么"，只有当时那棵树才解释得通
/// （比如 childList 只给被增删的节点，不给它在兄弟里的位置；移除更是只给节点不给父节点）。
/// 把原始记录透传过去，接收方只能靠选择器去猜，猜不到就悄悄跳过 —— 页面就此永久跑偏。
/// 这里的每条操作都带**节点 id**，是发送方在自己的权威状态上算出来的，接收方按 id 直接落。
///
/// 操作按数组顺序逐条应用，顺序不能重排。对 <c>insert</c> 尤其严格：rrweb 产出的 adds 里
/// **父节点一定排在子节点前面、锚点节点一定排在被插入的节点前面**，接收方必须照这个顺序落，
/// 否则会去引用一个还没建出来的 id。
///
/// 没有 `move` 这个种类，是因为**线上根本不存在**：rrweb 处理节点移动时会把配对的
/// remove 消掉、只发一条 add（`record/mutation.ts` 里 `movedMap` 那段）。接收方拿到一条
/// add 时，如果 id 已经在自己的镜像里，rrweb 的 <c>rebuild()</c> 会直接返回那个**已有的 DOM 节点**
/// 而不是新建一个 —— 前提是接收方把自己的镜像传给了 rebuild。所以移动在接收端就是一次
/// 真正的 DOM 移动，不需要单独的 op 种类。
/// </summary>
public class DomOpDto
{
    /// <summary>操作种类，见 <see cref="DomOpKinds"/>。</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>
    /// 目标节点 id。
    /// `insert` 时是**父节点**（插到它下面）；`remove` / `attrs` / `text` 都是**被操作的那个节点本人**。
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// `remove`：发送方认为这个节点的父节点是谁。
    /// 接收方可以用来做一次廉价的交叉校验 —— 实际父节点对不上就说明两端已经分叉，
    /// 该重新同步而不是继续删。其余操作不用这个字段。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Parent { get; set; }

    /// <summary>
    /// `insert`：插入位置的**锚点节点 id** —— 新节点插到它前面。为 null 表示追加到父节点末尾。
    ///
    /// 用 id 而不是下标：两端的兄弟列表只要有一点不同步，下标就指到别的缝里去；
    /// id 指错了会**指不到**（接收方立刻能发现），而不是指到隔壁那个无辜的节点上。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Next { get; set; }

    /// <summary>
    /// `insert`：要插入的子树（可以是一段连续兄弟）。id 由发送方分配，接收方照单收下。
    /// 元素取标签与属性、文本取内容 —— 不再走 HTML 字符串，所以不存在"转义/截断把标签弄坏"。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SerializedNodeDto>? Nodes { get; set; }

    /// <summary>`attrs`：要设置的属性（含新值）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Set { get; set; }

    /// <summary>`attrs`：要删除的属性名。属性删除与"设成空串"是两件事，这里分得开。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Remove { get; set; }

    /// <summary>`text`：文本节点的新内容。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
}

/// <summary>操作种类常量。</summary>
public static class DomOpKinds
{
    /// <summary>在父节点（<see cref="DomOpDto.Id"/>）的锚点（<see cref="DomOpDto.Next"/>）之前插入一棵子树。</summary>
    public const string Insert = "insert";

    /// <summary>摘掉一个节点（连同它的子树）。</summary>
    public const string Remove = "remove";

    /// <summary>增删属性。</summary>
    public const string Attrs = "attrs";

    /// <summary>改文本节点的内容。</summary>
    public const string Text = "text";
}
