namespace KiriyamaShareBackend.Infrastructure.Sharing;

/// <summary>
/// 共享服务的配置，对应 <c>Configurations/share.json</c> 的 <c>share</c> 段。
/// 所有项都有默认值，配置缺失也能正常工作。
/// </summary>
public class ShareHubSettings
{
    /// <summary>Default section name.</summary>
    public const string Position = "share";

    /// <summary>是否启用共享 Hub。关掉则 <c>/share</c> 不挂载。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>房间号长度。用户要手抄，别太长。</summary>
    public int RoomCodeLength { get; set; } = 6;

    /// <summary>同时存在的房间上限。匿名连接也能建房，必须有个上限兜住内存。</summary>
    public int MaxRooms { get; set; } = 500;

    /// <summary>单个连接最多订阅多少个房间。</summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 20;

    /// <summary>一个 patch 里最多多少条操作，超出的直接丢掉（客户端侧也有熔断）。</summary>
    public int MaxOpsPerPatch { get; set; } = 500;

    /// <summary>单个属性值 / 文本的最大长度，超出截断。</summary>
    public int MaxValueLength { get; set; } = 4096;

    /// <summary>
    /// 一条 `insert` 里最多多少个节点，超出的子树直接不插（并记日志）。
    ///
    /// 单独一项、不复用 <see cref="MaxValueLength"/>：那个是给文本/属性用的，
    /// 谁把它调小都不该顺手把"新增元素能不能复现"弄坏。
    /// </summary>
    public int MaxInsertNodes { get; set; } = 2000;

    /// <summary>
    /// 整页基线最多多少个节点，超出就裁剪（并在 <see cref="DomSnapshotDto.Truncated"/> 上标出来）。
    /// 基线每个房间常驻一份，必须有个上限兜住内存。
    /// </summary>
    public int MaxSnapshotNodes { get; set; } = 20_000;

    /// <summary>
    /// 整页基线的文本 / 属性字符预算，超出就裁剪。
    ///
    /// 只有节点数上限是不够的：两万个节点每个都挂 4K 文本就是 80M 字符。
    /// 这一项才是真正兜住内存的那个数。
    /// </summary>
    public int MaxSnapshotChars { get; set; } = 1_500_000;

    /// <summary>
    /// SignalR 单条消息的接收上限（字节）。
    ///
    /// 基线走的是**结构化节点树**而不是 HTML 字符串：每个节点要带 `id` / `tag` / `attrs` 几个键，
    /// 所以同样一棵树比 `outerHTML` 大。这里的关键是**把它对齐到 <see cref="MaxSnapshotChars"/> 的最坏情况**，
    /// 否则服务端会宣告"我收得下这份快照"，而传输层根本运不过来 ——
    /// 消息超限时 SignalR 直接把连接 abort 掉，房主只拿到一条 HubException，
    /// 表现为"房间刚建好就没了"，且日志里看不到原因。
    ///
    /// 算式（按 <see cref="MaxSnapshotChars"/> 顶格算）：
    ///   1 500 000 字符 × 3 字节（中文在 UTF-8 里一个字 3 字节）= 4.5 MB
    /// + 20 000 个节点的结构开销（`id` / `type` / `tagName` / 括号逗号，约 48 B/节点）= 1.0 MB
    /// + 属性名的引号与标点（**不计入字符预算**，所以只能靠余量兜）= 1 MB 量级
    /// ≈ 6.5 MB，取 8 MiB 留出余量。
    ///
    /// 注意：**字符预算无法严格限制字节数**。`MaxAttributesPerNode` 是 256，
    /// 两万个节点就算只挂短属性，光是引号标点也能堆出好几 MB 来，
    /// 而那部分不进 <see cref="MaxSnapshotChars"/>。所以真正兜住连接的是
    /// 采集端那条按 UTF-8 字节量的发送闸（见 `background.ts` 的 MAX_UPLINK_BYTES），
    /// 这一项只是保证"服务端自己认为合法的快照"能运得进来。
    /// </summary>
    public int MaximumReceiveMessageSize { get; set; } = 8_388_608;

    /// <summary>
    /// 单条 Hub 消息里 JSON 允许的最大嵌套层数。
    ///
    /// System.Text.Json 默认 64 —— 对普通消息绰绰有余，对**节点树**远远不够：
    /// 树里每下一层 DOM 要花两层 JSON（<c>childNodes</c> 数组 + 节点对象），
    /// 再加上信封（消息对象 → <c>arguments</c> 数组 → DTO → <c>root</c>，共 4 层），
    /// 64 层只够约 **30 层 DOM** 深。真实页面（尤其是脚本渲染完的）随便就比这深，
    /// 于是 <c>JsonHubProtocol.ParseMessage</c> 抛 <c>JsonReaderException</c>。
    ///
    /// 要命的是 SignalR 对"解析不了的消息"的处理是**把连接 abort 掉**，不是拒收这一条。
    /// 房间的寿命等于房主的连接，所以表现出来就是"刚一开始分享，房间就没了"，
    /// 而客户端只拿到一句 "Connection closed with an error" —— 完全看不出是深度问题。
    ///
    /// 512 层 ≈ 254 层 DOM，远超任何真实页面；同时又是个有限数
    /// （<c>Utf8JsonReader</c> 会按这个值分配内部 bitstack，不能设成"无限"）。
    /// </summary>
    public int MaxJsonDepth { get; set; } = 512;

    /// <summary>成员光标配色，按房间内不重样的顺序分配。</summary>
    public string[] PeerColors { get; set; } =
    [
        "#e0533d", "#3d7de0", "#2f9e6b", "#b57ce0",
        "#d9a13b", "#3fb6c6", "#e0619b", "#7d8fa8",
    ];
}
