using System.Text.Json.Serialization;

namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// rrweb 的 <c>NodeType</c> 枚举。线上是数字，这里给名字，免得整份代码里散布魔法数。
///
/// **数值顺序必须与 rrweb 的 <c>NodeType</c> 完全一致**（0..5）——
/// 我们是把 rrweb 的形状直接当线格式用，接收方会把它原样喂给 rrweb 的 <c>rebuild()</c>。
/// </summary>
public static class SerializedNodeTypes
{
    /// <summary>整个文档（整页快照的根就是这个）。</summary>
    public const int Document = 0;

    /// <summary>&lt;!DOCTYPE&gt;：内容在 Name / PublicId / SystemId 上。</summary>
    public const int DocumentType = 1;

    /// <summary>元素。</summary>
    public const int Element = 2;

    /// <summary>文本节点。</summary>
    public const int Text = 3;

    /// <summary>CDATA（只在 XML/SVG 文档里出现）。</summary>
    public const int CData = 4;

    /// <summary>注释节点。</summary>
    public const int Comment = 5;
}

/// <summary>
/// 序列化后的一个 DOM 节点，**带上传送方分配的节点 id**。
///
/// 形状直接对齐 rrweb 的 <c>serializedNodeWithId</c>（<c>@rrweb/types</c>），因为观众端要把它
/// 原样交给 rrweb 的 <c>rebuild()</c> 去做重建 —— 少一个字段，重建出来的页面就少一点东西，
/// 而且**不会报错**。字段名用 <see cref="JsonPropertyNameAttribute"/> 映射到 rrweb 的线上名字，
/// C# 属性名保留我们内部的短名（Tag / Text / Attrs / Children），这样注册表里的裁剪与克隆代码不用跟着改名。
///
/// 这跟旧方案（HTML 字符串 + <c>tag &gt; tag:nth-of-type(n)</c> 选择器路径）的区别在于**身份**：
/// 选择器是"按位置猜"，两端结构差一丝就指向错的节点，而且猜错了不报错；id 是两端共用的身份，
/// 对不上只能说明链路丢了东西 —— 那时候该重新同步，而不是继续在错的树上叠增量。
///
/// <see cref="Type"/> 是**必需字段**，不是可省略的装饰：文本节点与注释节点在 rrweb 的形状里
/// **只靠 type 区分**（两者的 Text 都是字符串），只凭"有没有 Tag"判断会把注释当成文本、
/// 在观众端重建出错误的节点类型。
/// </summary>
public class SerializedNodeDto
{
    /// <summary>节点 id。元素、文本、注释共用一套编号空间；由发送方的镜像分配、单调递增、永不复用。</summary>
    public int Id { get; set; }

    /// <summary>节点种类，见 <see cref="SerializedNodeTypes"/>。**判定节点类型以它为准**，不看 Tag 是否为 null。</summary>
    public int Type { get; set; }

    /// <summary>元素标签名。非元素节点为 null。</summary>
    [JsonPropertyName("tagName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; set; }

    /// <summary>文本 / 注释 / CDATA 的内容。元素与文档节点为 null。</summary>
    [JsonPropertyName("textContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>
    /// 元素属性。**值一律是字符串**：rrweb 的 <c>rebuild()</c> 落属性时本来就会 <c>String(value)</c> 一次，
    /// 所以发送方（前端翻译层）把数字/布尔值统一转成字符串是无损的，
    /// 换来的是这个字典不需要处理 JSON 的各种标量类型。
    /// 没有属性时为 null（不写空对象，省流量）。
    /// </summary>
    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Attrs { get; set; }

    /// <summary>子节点。没有子节点时为 null。</summary>
    [JsonPropertyName("childNodes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SerializedNodeDto>? Children { get; set; }

    /// <summary>文档节点：<c>BackCompat</c> 表示怪异模式。rebuild 靠它决定要不要补 doctype。</summary>
    [JsonPropertyName("compatMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompatMode { get; set; }

    /// <summary>DOCTYPE 的名字（通常是 <c>html</c>）。</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>DOCTYPE 的 publicId。</summary>
    [JsonPropertyName("publicId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicId { get; set; }

    /// <summary>DOCTYPE 的 systemId。</summary>
    [JsonPropertyName("systemId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemId { get; set; }

    /// <summary>元素属于 SVG 命名空间。rrweb 只在为真时才写这个字段。</summary>
    [JsonPropertyName("isSVG")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSvg { get; set; }

    /// <summary>元素带 <c>rr-block</c>（被采集方显式排除），重建时要跳过它的内容。</summary>
    [JsonPropertyName("needBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NeedBlock { get; set; }

    /// <summary>自定义元素（带 <c>-</c> 的标签名）。</summary>
    [JsonPropertyName("isCustom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsCustom { get; set; }

    /// <summary>这个节点属于哪个根（主文档为 0，iframe / shadow root 各有自己的根）。</summary>
    [JsonPropertyName("rootId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RootId { get; set; }

    /// <summary>这个元素挂着 shadow root。</summary>
    [JsonPropertyName("isShadowHost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsShadowHost { get; set; }

    /// <summary>这个节点本身在 shadow root 里。</summary>
    [JsonPropertyName("isShadow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsShadow { get; set; }

    /// <summary>是不是文本节点。</summary>
    [JsonIgnore]
    public bool IsText => Type == SerializedNodeTypes.Text;

    /// <summary>是不是元素节点。</summary>
    [JsonIgnore]
    public bool IsElement => Type == SerializedNodeTypes.Element;

    /// <summary>是不是文档节点。</summary>
    [JsonIgnore]
    public bool IsDocument => Type == SerializedNodeTypes.Document;

    /// <summary>是不是能盖住整个页面的根（文档或元素）。别的东西当根都只会在观众端重建出一片空白。</summary>
    [JsonIgnore]
    public bool CanBeRoot =>
        Type == SerializedNodeTypes.Document
        || (Type == SerializedNodeTypes.Element && !string.IsNullOrEmpty(Tag));

    /// <summary>
    /// 把一个节点的**标量字段**复制到本实例上（不含 <see cref="Children"/>）。
    ///
    /// 裁剪与深拷贝都要复制这一堆字段，写两遍必然会漏掉某个（漏了只是线上少个字段，
    /// 观众端悄悄少一点东西，不会报错）。所以集中在这里。
    /// </summary>
    /// <param name="source">来源节点。</param>
    public void CopyScalarsFrom(SerializedNodeDto source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        Type = source.Type;
        Tag = source.Tag;
        Text = source.Text;
        Attrs = source.Attrs is null
            ? null
            : new Dictionary<string, string>(source.Attrs, StringComparer.Ordinal);
        CompatMode = source.CompatMode;
        Name = source.Name;
        PublicId = source.PublicId;
        SystemId = source.SystemId;
        IsSvg = source.IsSvg;
        NeedBlock = source.NeedBlock;
        IsCustom = source.IsCustom;
        RootId = source.RootId;
        IsShadowHost = source.IsShadowHost;
        IsShadow = source.IsShadow;
    }
}
