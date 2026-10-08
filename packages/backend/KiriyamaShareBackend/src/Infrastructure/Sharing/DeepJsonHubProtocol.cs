using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;

namespace KiriyamaShareBackend.Infrastructure.Sharing;

/// <summary>
/// 和官方 <c>JsonHubProtocol</c> **线格式完全一致**，但 JSON 嵌套上限可配。
///
/// 为什么非要有这个东西：
/// 官方那个协议把 64 层写死且**没有开关** —— 类 <c>sealed</c>、<c>ParseMessage</c> 是 <c>private</c>、
/// reader 建在 <c>new Utf8JsonReader(input, isFinalBlock: true, state: default)</c> 上（走默认
/// <c>JsonReaderOptions</c>）。<c>AddJsonProtocol(o =&gt; o.PayloadSerializerOptions.MaxDepth = N)</c>
/// **只影响写、不影响读**（只有 <c>SerializeObject</c> 用那份 options）。
/// 换 Newtonsoft 版也没用：读的闸在 <c>JsonReader</c> 基类构造里（<c>_maxDepth = 64</c>），
/// 而 SignalR 自己的 <c>JsonUtils.CreateJsonTextReader</c> 不抬它。
///
/// 而我们的基线是**节点树**：信封 4 层（消息对象 → <c>arguments</c> 数组 → DTO → <c>root</c>），
/// 之后每层 DOM 再花 2 层（<c>childNodes</c> 数组 + 节点对象）—— 64 层只够约 **30 层 DOM**。
/// 真实页面（脚本渲染完的）随便就比这深。超了以后 SignalR **不是拒收这条消息，是把连接 abort 掉**；
/// 房间的寿命等于房主的连接，于是表现成"刚开始分享，房间就没了"，
/// 客户端只拿到一句 "Connection closed with an error"，服务端日志里只有 "Error reading JSON"。
///
/// 实现策略：**写**直接委托官方实现（线格式因此逐字节不变），**读**自己走一遍，
/// 只把 reader 的 <c>MaxDepth</c> 换成配置值。也就是说客户端、DTO、注册表裁剪逻辑全都不用动。
///
/// 解析逻辑照官方 <c>JsonHubProtocol.ParseMessage</c> 抄，改动只有两处：
/// 1. reader 用 <see cref="_readerOptions"/> 构造，深度可配；
/// 2. 不处理只有 client results 才用得到的 <c>sequenceId</c> 与 <c>RawResult</c>（本项目不用）。
/// </summary>
internal sealed class DeepJsonHubProtocol : IHubProtocol
{
    /// <summary>SignalR 文本帧里长度前缀与正文之间的分隔符（RS）。</summary>
    private const byte RecordSeparator = 0x1e;

    private const string TypePropertyName = "type";
    private const string InvocationIdPropertyName = "invocationId";
    private const string TargetPropertyName = "target";
    private const string ArgumentsPropertyName = "arguments";
    private const string StreamIdsPropertyName = "streamIds";
    private const string ItemPropertyName = "item";
    private const string ResultPropertyName = "result";
    private const string ErrorPropertyName = "error";
    private const string AllowReconnectPropertyName = "allowReconnect";
    private const string HeadersPropertyName = "headers";

    // 与 HubProtocolConstants 同值。那几个常量在别处且不保证公开，这里显式写死并注明来源。
    private const int InvocationMessageType = 1;
    private const int StreamItemMessageType = 2;
    private const int CompletionMessageType = 3;
    private const int StreamInvocationMessageType = 4;
    private const int CancelInvocationMessageType = 5;
    private const int PingMessageType = 6;
    private const int CloseMessageType = 7;

    /// <summary>官方实现，只用来**写**消息，保证线上格式一字不差。</summary>
    private readonly JsonHubProtocol _writer;

    /// <summary>只用来**读**消息的 reader 选项，深度来自配置。</summary>
    private readonly JsonReaderOptions _readerOptions;

    /// <summary>
    /// 参数绑定用的序列化配置。
    ///
    /// 刻意从 <see cref="JsonHubProtocolOptions"/> 的默认值出发、只改 <c>MaxDepth</c>：
    /// camelCase、大小写不敏感、不忽略 null、宽松转义……这些全都得和官方一致，
    /// 差一个都会让线上的字段名或形态变化，客户端就认不出来了。
    /// </summary>
    private readonly JsonSerializerOptions _payloadOptions;

    /// <summary>Initializes a new instance of the <see cref="DeepJsonHubProtocol"/> class.</summary>
    /// <param name="settings">共享模块配置，这里只取 <see cref="ShareHubSettings.MaxJsonDepth"/>。</param>
    public DeepJsonHubProtocol(IOptions<ShareHubSettings> settings)
    {
        var depth = settings.Value.MaxJsonDepth;

        var options = new JsonHubProtocolOptions();
        options.PayloadSerializerOptions.MaxDepth = depth;

        _payloadOptions = options.PayloadSerializerOptions;
        _writer = new JsonHubProtocol(Options.Create(options));
        _readerOptions = new JsonReaderOptions
        {
            MaxDepth = depth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        };
    }

    /// <inheritdoc/>
    public string Name => "json";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public TransferFormat TransferFormat => TransferFormat.Text;

    /// <inheritdoc/>
    public bool IsVersionSupported(int version) => version <= Version;

    /// <inheritdoc/>
    public void WriteMessage(HubMessage message, IBufferWriter<byte> output) => _writer.WriteMessage(message, output);

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message) =>
        ((IHubProtocol)_writer).GetMessageBytes(message);

    /// <inheritdoc/>
    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [NotNullWhen(true)] out HubMessage? message)
    {
        if (!TryParseFrame(ref input, out var payload))
        {
            // 帧还没收全，让上层继续读
            message = null;
            return false;
        }

        message = ParseMessage(payload, binder);
        return message is not null;
    }

    /// <summary>
    /// 拆出 SignalR 文本协议的一帧。
    ///
    /// 线格式就是 **<c>&lt;UTF-8 正文&gt; 0x1E</c>**，**没有长度前缀**。
    /// （官方 <c>TextMessageParser</c> 也就是"找分隔符、切开"这么两步；
    /// 这里先前按"长度前缀 + 分隔符"写过一版，客户端第一条消息就被打死 —— 别再加回来。）
    /// </summary>
    private static bool TryParseFrame(ref ReadOnlySequence<byte> input, out ReadOnlySequence<byte> payload)
    {
        payload = default;

        var separator = input.PositionOf(RecordSeparator);
        if (separator is null)
        {
            // 分隔符还没到：这一帧还没收全，让上层继续读
            return false;
        }

        payload = input.Slice(input.Start, separator.Value);
        input = input.Slice(input.GetPosition(1, separator.Value));
        return true;
    }

    /// <summary>把一帧正文解析成 Hub 消息。结构照官方实现。</summary>
    private HubMessage? ParseMessage(ReadOnlySequence<byte> payload, IInvocationBinder binder)
    {
        // 唯一和官方不同的地方：reader 带上我们自己的 MaxDepth。
        // 注意 JSON 协议这条路上没有 `(ReadOnlySequence&lt;byte&gt;, JsonReaderOptions)` 重载，
        // 所以多段时要先拼成连续内存才能把选项传进去。
        var reader = payload.IsSingleSegment
            ? new Utf8JsonReader(payload.First.Span, _readerOptions)
            : new Utf8JsonReader(payload.ToArray(), _readerOptions);

        int? type = null;
        string? invocationId = null;
        string? target = null;
        string? error = null;

        var hasArguments = false;
        object?[]? arguments = null;
        var hasArgumentsToken = false;
        var argumentsToken = default(Utf8JsonReader);

        var hasItem = false;
        object? item = null;
        var hasItemToken = false;
        var itemToken = default(Utf8JsonReader);

        var hasResult = false;
        object? result = null;

        string[]? streamIds = null;
        Dictionary<string, string>? headers = null;
        var allowReconnect = false;
        ExceptionDispatchInfo? argumentBindingException = null;

        ReadNext(ref reader);
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException($"Expected 'StartObject' but got '{reader.TokenType}'.");
        }

        var completed = false;
        do
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (reader.ValueTextEquals("type"u8))
                    {
                        type = ReadAsInt32(ref reader, TypePropertyName);
                    }
                    else if (reader.ValueTextEquals("invocationId"u8))
                    {
                        invocationId = ReadAsString(ref reader, InvocationIdPropertyName);
                    }
                    else if (reader.ValueTextEquals("target"u8))
                    {
                        target = ReadAsString(ref reader, TargetPropertyName);
                    }
                    else if (reader.ValueTextEquals("error"u8))
                    {
                        error = ReadAsString(ref reader, ErrorPropertyName);
                    }
                    else if (reader.ValueTextEquals("allowReconnect"u8))
                    {
                        allowReconnect = ReadAsBoolean(ref reader, AllowReconnectPropertyName);
                    }
                    else if (reader.ValueTextEquals("streamIds"u8))
                    {
                        streamIds = ReadStreamIds(ref reader);
                    }
                    else if (reader.ValueTextEquals("headers"u8))
                    {
                        ReadNext(ref reader);
                        headers = ReadHeaders(ref reader);
                    }
                    else if (reader.ValueTextEquals("arguments"u8))
                    {
                        ReadNext(ref reader);
                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            throw new InvalidDataException($"Expected '{ArgumentsPropertyName}' to be of type StartArray.");
                        }

                        hasArguments = true;

                        if (string.IsNullOrEmpty(target))
                        {
                            // 还不知道 target，参数类型得问 binder，所以现在绑不了。
                            // Utf8JsonReader 是 struct，直接值拷贝就是快照；等读完 target 再回来绑。
                            hasArgumentsToken = true;
                            argumentsToken = reader;
                            reader.Skip();
                        }
                        else
                        {
                            try
                            {
                                arguments = BindTypes(ref reader, binder.GetParameterTypes(target));
                            }
                            catch (Exception ex)
                            {
                                argumentBindingException = ExceptionDispatchInfo.Capture(ex);

                                // 出错时 reader 停在数组中间，先把整个数组跳掉，后面的属性才读得下去。
                                // 条件是官方那版：要么还停在 StartArray（一个元素都没读），要么已经进到里面。
                                var initialDepth = reader.CurrentDepth;
                                while ((reader.CurrentDepth == initialDepth && reader.TokenType == JsonTokenType.StartArray)
                                       || reader.CurrentDepth > initialDepth)
                                {
                                    ReadNext(ref reader);
                                }
                            }
                        }
                    }
                    else if (reader.ValueTextEquals("item"u8))
                    {
                        ReadNext(ref reader);
                        hasItem = true;

                        if (string.IsNullOrEmpty(invocationId))
                        {
                            hasItemToken = true;
                            itemToken = reader;
                            reader.Skip();
                        }
                        else
                        {
                            try
                            {
                                item = BindType(ref reader, binder.GetStreamItemType(invocationId));
                            }
                            catch (Exception ex)
                            {
                                return new StreamBindingFailureMessage(invocationId, ExceptionDispatchInfo.Capture(ex));
                            }
                        }
                    }
                    else if (reader.ValueTextEquals("result"u8))
                    {
                        ReadNext(ref reader);
                        hasResult = true;

                        Type? returnType = null;
                        if (!string.IsNullOrEmpty(invocationId))
                        {
                            try
                            {
                                returnType = binder.GetReturnType(invocationId);
                            }
                            catch (Exception)
                            {
                                // binder 不认识这个 invocationId：当成没有返回值
                                returnType = null;
                            }
                        }

                        if (returnType is null)
                        {
                            // `result` 只有 client results 才用得到，本项目不用：
                            // 不知道 invocationId 就直接跳掉，不留 reader 快照。
                            reader.Skip();
                            result = null;
                        }
                        else
                        {
                            try
                            {
                                result = BindType(ref reader, returnType);
                            }
                            catch (JsonException ex)
                            {
                                error = $"Error trying to deserialize result to {returnType.Name}. {ex.Message}";
                                hasResult = false;
                            }
                        }
                    }
                    else
                    {
                        // 未知属性（含 client results 的 sequenceId）整个跳掉
                        ReadNext(ref reader);
                        reader.Skip();
                    }

                    break;

                case JsonTokenType.EndObject:
                    completed = true;
                    break;
            }
        }
        while (!completed && ReadNext(ref reader));

        var message = BuildMessage(
            type,
            binder,
            invocationId,
            target,
            error,
            hasArguments,
            arguments,
            hasArgumentsToken,
            argumentsToken,
            hasItem,
            item,
            hasItemToken,
            itemToken,
            hasResult,
            result,
            streamIds,
            allowReconnect,
            argumentBindingException);

        ApplyHeaders(message, headers);
        return message;
    }

    /// <summary>按 <c>type</c> 组装出具体的 Hub 消息。</summary>
    private HubMessage BuildMessage(
        int? type,
        IInvocationBinder binder,
        string? invocationId,
        string? target,
        string? error,
        bool hasArguments,
        object?[]? arguments,
        bool hasArgumentsToken,
        Utf8JsonReader argumentsToken,
        bool hasItem,
        object? item,
        bool hasItemToken,
        Utf8JsonReader itemToken,
        bool hasResult,
        object? result,
        string[]? streamIds,
        bool allowReconnect,
        ExceptionDispatchInfo? argumentBindingException)
    {
        switch (type)
        {
            case InvocationMessageType:
            case StreamInvocationMessageType:
                {
                    if (string.IsNullOrEmpty(target))
                    {
                        throw new InvalidDataException($"Missing required property '{TargetPropertyName}'.");
                    }

                    if (hasArgumentsToken)
                    {
                        // 参数出现在 target 之前，现在才绑得了
                        try
                        {
                            arguments = BindTypes(ref argumentsToken, binder.GetParameterTypes(target));
                        }
                        catch (Exception ex)
                        {
                            argumentBindingException = ExceptionDispatchInfo.Capture(ex);
                        }
                    }

                    if (!hasArguments)
                    {
                        throw new InvalidDataException($"Missing required property '{ArgumentsPropertyName}'.");
                    }

                    if (argumentBindingException is not null)
                    {
                        return new InvocationBindingFailureMessage(invocationId, target, argumentBindingException);
                    }

                    var bound = arguments ?? Array.Empty<object?>();

                    return type == InvocationMessageType
                        ? new InvocationMessage(invocationId, target, bound, streamIds)
                        : new StreamInvocationMessage(invocationId, target, bound, streamIds);
                }

            case StreamItemMessageType:
                {
                    if (invocationId is null)
                    {
                        throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
                    }

                    if (!hasItem)
                    {
                        throw new InvalidDataException($"Missing required property '{ItemPropertyName}'.");
                    }

                    if (hasItemToken)
                    {
                        try
                        {
                            item = BindType(ref itemToken, binder.GetStreamItemType(invocationId));
                        }
                        catch (Exception ex)
                        {
                            return new StreamBindingFailureMessage(invocationId, ExceptionDispatchInfo.Capture(ex));
                        }
                    }

                    return new StreamItemMessage(invocationId, item);
                }

            case CompletionMessageType:
                {
                    if (invocationId is null)
                    {
                        throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
                    }

                    // 注意参数顺序是 (invocationId, error, result, hasResult)
                    return new CompletionMessage(invocationId, error, result, hasResult);
                }

            case CancelInvocationMessageType:
                {
                    if (invocationId is null)
                    {
                        throw new InvalidDataException($"Missing required property '{InvocationIdPropertyName}'.");
                    }

                    return new CancelInvocationMessage(invocationId);
                }

            case PingMessageType:
                return PingMessage.Instance;

            case CloseMessageType:
                return new CloseMessage(error, allowReconnect);

            default:
                throw new InvalidDataException($"Unknown message type {type}.");
        }
    }

    /// <summary>
    /// 挂上 headers。
    ///
    /// Invocation / StreamInvocation / CancelInvocation / StreamItem / Completion 都继承
    /// <see cref="HubInvocationMessage"/>，Headers 就定义在它上面（基类 <c>HubMessage</c> 没有）。
    /// <c>CloseMessage</c> 在 .NET 9 上没有 Headers，带不了 —— 官方实现同样带不了。
    /// </summary>
    private static void ApplyHeaders(HubMessage message, Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return;
        }

        if (message is HubInvocationMessage invocation)
        {
            invocation.Headers = headers;
        }
    }

    /// <summary>读一个 JSON 值并绑成 <paramref name="type"/>。</summary>
    private object? BindType(ref Utf8JsonReader reader, Type type) =>
        JsonSerializer.Deserialize(ref reader, type, _payloadOptions);

    /// <summary>把参数数组里的每个值按 <paramref name="paramTypes"/> 绑定。</summary>
    private object?[] BindTypes(ref Utf8JsonReader reader, IReadOnlyList<Type> paramTypes)
    {
        object?[]? arguments = null;
        var paramIndex = 0;
        var paramCount = paramTypes.Count;

        // 快照停在 StartArray 上，进去之后元素所在深度才会比它大
        var arrayDepth = reader.CurrentDepth;
        ReadNext(ref reader);

        while (reader.TokenType != JsonTokenType.EndArray && reader.CurrentDepth > arrayDepth)
        {
            if (paramIndex < paramCount)
            {
                arguments ??= new object?[paramCount];

                try
                {
                    arguments[paramIndex] = BindType(ref reader, paramTypes[paramIndex]);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        "Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.",
                        ex);
                }
            }
            else
            {
                // 多出来的参数读掉丢掉
                reader.Skip();
            }

            paramIndex++;
            ReadNext(ref reader);
        }

        return arguments ?? Array.Empty<object?>();
    }

    /// <summary>读 <c>streamIds</c>。</summary>
    private static string[] ReadStreamIds(ref Utf8JsonReader reader)
    {
        ReadNext(ref reader);
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new InvalidDataException($"Expected '{StreamIdsPropertyName}' to be of type StartArray.");
        }

        var ids = new List<string>();
        ReadNext(ref reader);

        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new InvalidDataException($"Null value for '{StreamIdsPropertyName}' is not valid.");
            }

            ids.Add(reader.GetString()!);
            ReadNext(ref reader);
        }

        return ids.ToArray();
    }

    /// <summary>读 <c>headers</c> 对象。调用时 reader 必须停在 StartObject。</summary>
    private static Dictionary<string, string>? ReadHeaders(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException($"Expected '{HeadersPropertyName}' to be of type StartObject.");
        }

        Dictionary<string, string>? headers = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    ReadNext(ref reader);

                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new InvalidDataException($"Expected header '{name}' to be of type String.");
                    }

                    headers ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    headers[name] = reader.GetString()!;
                    break;

                case JsonTokenType.EndObject:
                    return headers;

                default:
                    throw new InvalidDataException($"Unexpected token '{reader.TokenType}' while reading '{HeadersPropertyName}'.");
            }
        }

        throw new InvalidDataException($"Unexpected end of JSON while reading '{HeadersPropertyName}'.");
    }

    /// <summary>读下一个 token，读不到就当场炸 —— 静默截断比报错更难查。</summary>
    private static bool ReadNext(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new InvalidDataException("Unexpected end when reading JSON message.");
        }

        return true;
    }

    private static int ReadAsInt32(ref Utf8JsonReader reader, string propertyName)
    {
        ReadNext(ref reader);
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"Expected '{propertyName}' to be of type Number.");
        }

        return value;
    }

    private static string ReadAsString(ref Utf8JsonReader reader, string propertyName)
    {
        ReadNext(ref reader);
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new InvalidDataException($"Expected '{propertyName}' to be of type String.");
        }

        return reader.GetString()!;
    }

    private static bool ReadAsBoolean(ref Utf8JsonReader reader, string propertyName)
    {
        ReadNext(ref reader);
        if (reader.TokenType is not JsonTokenType.True and not JsonTokenType.False)
        {
            throw new InvalidDataException($"Expected '{propertyName}' to be of type Boolean.");
        }

        return reader.GetBoolean();
    }
}
