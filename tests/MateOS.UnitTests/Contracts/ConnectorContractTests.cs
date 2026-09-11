using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MateOS.Contracts.Protocol;

// 与 System.Threading.ExecutionContext 同名（implicit usings 会引入），必须显式消歧
using ExecutionContext = MateOS.Contracts.Protocol.ExecutionContext;

namespace MateOS.UnitTests.Contracts;

/// <summary>
/// 契约 SSOT 的可执行校验：<c>contracts/schemas/**</c> 与 <c>MateOS.Contracts</c> 必须一致。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要这个测试：契约若只是文档，实现与它分叉时没有任何东西会失败。
/// 这里把「C# 契约记录序列化后的 JSON」真的喂给 JSON Schema 校验，
/// 于是<b>改字段名/形状会同时触发 schema 测试失败</b>，而不是等到客户端集成才发现。
/// </para>
/// <para>
/// 两条投递通道（WS 推送 与 HTTP 轮询 inbox）必须产出<b>逐字节一致</b>的 payload：
/// 它们共享同一份事实，SDK 只应有一份解析代码。
/// </para>
/// </remarks>
public sealed class ConnectorContractTests
{
    /// <summary>与服务端一致的序列化选项（REST 全局策略 + 推送 payload 选项）。</summary>
    private static readonly JsonSerializerOptions s_snakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ───────────────────── schema 自身 ─────────────────────

    [Fact]
    public void 所有schema文件都应是合法的JSON且声明2020_12()
    {
        string[] files = [.. Directory.EnumerateFiles(SchemaRoot, "*.json", SearchOption.AllDirectories)];

        // 防止样例文件被误删后测试变成"零覆盖还全绿"
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));

            Assert.Equal("https://json-schema.org/draft/2020-12/schema",
                doc.RootElement.GetProperty("$schema").GetString());

            Assert.True(doc.RootElement.TryGetProperty("$id", out _),
                $"{Path.GetFileName(file)} 缺少 $id（跨文件 $ref 需要它）");
        }
    }

    // ───────────────────── envelope ─────────────────────

    [Theory]
    [InlineData("execution.dispatch")]
    [InlineData("execution.dispatch_ack")]
    [InlineData("execution.resume_request")]
    [InlineData("execution.resume_ack")]
    [InlineData("collaboration.decision")]
    [InlineData("status")]
    public void envelope应接受全部契约类型(string type)
    {
        JsonNode envelope = EnvelopeNode(type, new JsonObject());

        Assert.True(Validate("connector/envelope.json", envelope).IsValid);
    }

    [Fact]
    public void envelope应拒绝未知type()
    {
        JsonNode envelope = EnvelopeNode("execution.teleport", new JsonObject());

        Assert.False(Validate("connector/envelope.json", envelope).IsValid);
    }

    /// <summary>
    /// id 必须同时接受 N 格式（服务端自身生成的 32 位无连字符）与
    /// D 格式（客户端常见），否则服务端发的帧会被自家 schema 判为非法。
    /// </summary>
    [Theory]
    [InlineData("6a8e1b8e9d3a4d3e8a4d3a8e1b8e9d3a")]
    [InlineData("6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a")]
    public void envelope的id应接受两种UUID写法(string id)
    {
        JsonNode envelope = new JsonObject
        {
            ["id"] = id,
            ["type"] = EnvelopeTypes.ExecutionDispatch,
            ["ts"] = 1_736_380_800_000,
            ["payload"] = new JsonObject(),
        };

        Assert.True(Validate("connector/envelope.json", envelope).IsValid);
    }

    [Fact]
    public void envelope应拒绝缺失id()
    {
        JsonNode envelope = new JsonObject
        {
            ["type"] = EnvelopeTypes.ExecutionDispatch,
            ["ts"] = 0,
            ["payload"] = new JsonObject(),
        };

        Assert.False(Validate("connector/envelope.json", envelope).IsValid);
    }

    // ───────────────────── execution.dispatch payload ─────────────────────

    [Fact]
    public void dispatch_payload的完整形状应通过校验()
    {
        JsonNode payload = Serialize(FullDispatchPayload());

        EvaluationResults results = Validate("execution/dispatch.json", payload);

        Assert.True(results.IsValid, Describe(results));
    }

    /// <summary>
    /// 可空字段（collaboration_request_id / work_item_ref / deadline_s）为 null 时也必须合法，
    /// 且必须<b>真的出现在 JSON 里</b>——用"字段缺失"表达"没有值"会让客户端
    /// 无法区分「不支持」与「本次为空」。
    /// </summary>
    [Fact]
    public void dispatch_payload的可空字段应为显式null且合法()
    {
        var payload = new ExecutionDispatchPayload(
            ExecutionId: Guid.NewGuid(),
            AttemptNo: 1,
            CollaborationRequestId: null,
            WorkItemRef: null,
            Input: new ExecutionInput(Prompt: null, Params: null),
            Context: new ExecutionContext(null, null, null, null),
            DeadlineS: null,
            IdempotencyKey: "api:1");

        JsonNode node = Serialize(payload);

        Assert.True(Validate("execution/dispatch.json", node).IsValid);

        foreach (string key in new[] { "collaboration_request_id", "work_item_ref", "deadline_s" })
        {
            Assert.True(node.AsObject().ContainsKey(key), $"payload 缺少 {key}");
            Assert.Null(node[key]);
        }
    }

    [Fact]
    public void work_item_ref应是对象而非裸id()
    {
        JsonNode node = Serialize(FullDispatchPayload());

        JsonNode workItemRef = node["work_item_ref"]!;

        Assert.Equal("builtin", workItemRef["provider_key"]!.GetValue<string>());

        // external_ref 的值可以是 null（builtin 没有外部标识），但<b>键必须存在</b>：
        // 缺键会让客户端无法区分「不支持该字段」与「本次为空」
        Assert.True(workItemRef.AsObject().ContainsKey("external_ref"));
        Assert.Null(workItemRef["external_ref"]);
    }

    /// <summary>attempt_no 必须存在：SDK 靠 (execution_id, attempt_no) 去重（detailed/10 §4）。</summary>
    [Fact]
    public void dispatch_payload必须携带attempt_no()
    {
        JsonNode node = Serialize(FullDispatchPayload());

        Assert.Equal(1, node["attempt_no"]!.GetValue<int>());
    }

    // ───────────────────── 两条通道同形 ─────────────────────

    /// <summary>
    /// WS 推送路径把 payload 序列化成 <see cref="JsonNode"/> 再嵌进 envelope，
    /// HTTP 轮询路径直接序列化 payload。两者必须逐字段一致。
    /// </summary>
    [Fact]
    public void WS推送与HTTP轮询的payload应逐字段一致()
    {
        ExecutionDispatchPayload payload = FullDispatchPayload();

        // HTTP：直接序列化 payload（REST 全局 snake_case 策略）
        JsonNode httpPath = JsonNode.Parse(JsonSerializer.Serialize(payload, s_snakeCase))!;

        // WS：payload → JsonNode 嵌入 envelope，与 AgentDispatchNotifier 同路径
        var envelope = new Envelope(
            Type: EnvelopeTypes.ExecutionDispatch,
            Id: Guid.NewGuid(),
            Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload: JsonSerializer.SerializeToNode(payload, s_snakeCase));

        JsonNode wsPath = JsonNode.Parse(JsonSerializer.Serialize(envelope, s_snakeCase))!["payload"]!;

        Assert.True(JsonNode.DeepEquals(httpPath, wsPath),
            $"两条通道 payload 不一致：\nHTTP = {httpPath.ToJsonString()}\nWS   = {wsPath.ToJsonString()}");
    }

    // ───────────────────── 其它消息 ─────────────────────

    [Fact]
    public void dispatch_ack_payload应通过校验()
    {
        var ack = new ExecutionDispatchAckPayload(
            ExecutionId: Guid.NewGuid(),
            AttemptNo: 1,
            Received: true,
            ProtocolError: null);

        Assert.True(Validate("execution/dispatch-ack.json", Serialize(ack)).IsValid);
    }

    [Fact]
    public void resume_ack_payload应通过校验并复用dispatch的input与context定义()
    {
        var ack = new ExecutionResumeAckPayload(
            ExecutionId: Guid.NewGuid(),
            AttemptNo: 1,
            LastPersistedSeq: 41,
            Snapshot: new ExecutionDispatchSnapshot(
                ExecutionId: Guid.NewGuid(),
                Input: new ExecutionInput("继续", null),
                Context: new ExecutionContext(null, null, null, null),
                DeadlineS: 600));

        EvaluationResults results = Validate("execution/resume-ack.json", Serialize(ack));

        Assert.True(results.IsValid, Describe(results));
    }

    [Fact]
    public void resume_ack的snapshot可为null()
    {
        var ack = new ExecutionResumeAckPayload(Guid.NewGuid(), 1, LastPersistedSeq: 0, Snapshot: null);

        Assert.True(Validate("execution/resume-ack.json", Serialize(ack)).IsValid);
    }

    [Fact]
    public void collaboration_decision_payload应通过校验()
    {
        var decision = new CollaborationDecisionPayload(
            CollaborationRequestId: Guid.NewGuid(),
            Decision: "ACCEPT",
            Reason: "capability match",
            Analysis: new DecisionAnalysis(Capability: true, ContextScore: 88, Permission: true),
            Needs: null);

        Assert.True(Validate("collaboration/decision.json", Serialize(decision)).IsValid);
    }

    [Fact]
    public void collaboration_decision应拒绝非法的analysis_context_score()
    {
        JsonNode node = new JsonObject
        {
            ["collaboration_request_id"] = Guid.NewGuid().ToString(),
            ["decision"] = "ACCEPT",
            ["reason"] = null,
            ["analysis"] = new JsonObject
            {
                ["capability"] = true,
                ["context_score"] = 120,   // 越界
                ["permission"] = true,
            },
            ["needs"] = null,
        };

        Assert.False(Validate("collaboration/decision.json", node).IsValid);
    }

    // ───────────────────── jsonb → 契约形状 的映射 ─────────────────────

    /// <summary>
    /// 已符合契约形状的存储内容应原样取用，不得二次包裹。
    /// </summary>
    [Fact]
    public void 映射对契约形状的存储内容应保持恒等()
    {
        ExecutionInput input = ExecutionPayloadMapper.ToInput(
            """{"prompt":"修复登录","params":{"file":"Auth.cs"}}""");

        Assert.Equal("修复登录", input.Prompt);
        Assert.Equal("Auth.cs", input.Params!["file"]!.GetValue<string>());
    }

    /// <summary>
    /// 非契约形状时：prompt 尽力提取，params 保留<b>整个</b>原始对象 —— 不允许丢信息。
    /// </summary>
    [Fact]
    public void 映射应对非契约形状保留全部原始信息()
    {
        ExecutionInput input = ExecutionPayloadMapper.ToInput(
            """{"work_item_id":"abc","project_id":"p1","title":"实现登录","type":"TASK"}""");

        Assert.Equal("实现登录", input.Prompt);

        // 原始四个字段一个都不能丢
        var parameters = input.Params!.AsObject();

        Assert.Equal("abc", parameters["work_item_id"]!.GetValue<string>());
        Assert.Equal("p1", parameters["project_id"]!.GetValue<string>());
        Assert.Equal("TASK", parameters["type"]!.GetValue<string>());
    }

    [Fact]
    public void 映射应把原始refs透传到context()
    {
        ExecutionContext context = ExecutionPayloadMapper.ToContext(
            """{"channel_id":"c1","message_seq":7}""");

        Assert.Null(context.MemoryRefs);
        Assert.Null(context.RecentMessages);
        Assert.Null(context.Permissions);
        Assert.Equal(7, context.Refs!["message_seq"]!.GetValue<int>());
    }

    [Fact]
    public void 映射应容忍空与非法JSON()
    {
        Assert.Null(ExecutionPayloadMapper.ToInput(null).Prompt);
        Assert.Null(ExecutionPayloadMapper.ToInput("").Params);

        // 非法 JSON 不猜测也不丢弃：原文以字符串形式带走
        ExecutionInput broken = ExecutionPayloadMapper.ToInput("not-json");
        Assert.Equal("not-json", broken.Params!.GetValue<string>());

        Assert.Null(ExecutionPayloadMapper.ToContext(null).Refs);
    }

    // ───────────────────── helpers ─────────────────────

    private static ExecutionDispatchPayload FullDispatchPayload() => new(
        ExecutionId: Guid.NewGuid(),
        AttemptNo: 1,
        CollaborationRequestId: Guid.NewGuid(),
        WorkItemRef: new WorkItemRef("builtin", Guid.NewGuid().ToString(), null),
        Input: new ExecutionInput(
            Prompt: "实现登录接口",
            Params: JsonNode.Parse("""{"title":"实现登录接口"}""")),
        Context: new ExecutionContext(
            MemoryRefs: null,
            RecentMessages: null,
            Permissions: null,
            Refs: JsonNode.Parse("""{"project_id":"p1","work_item_id":"w1"}""")),
        DeadlineS: 600,
        IdempotencyKey: "work-item:w1");

    private static JsonNode Serialize<T>(T value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, s_snakeCase))!;

    private static JsonNode EnvelopeNode(string type, JsonNode payload) => new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString("N"),
        ["type"] = type,
        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["payload"] = payload,
    };

    private static EvaluationResults Validate(string relativeSchemaPath, JsonNode instance)
    {
        JsonSchema schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(SchemaRoot, relativeSchemaPath)));

        return schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    private static string Describe(EvaluationResults results) => results.ToString();

    /// <summary>
    /// 仓库根下的 <c>contracts/schemas</c>。
    /// </summary>
    /// <remarks>
    /// 直接读仓库里的文件而不是复制到输出目录：契约校验的价值就在于「测试读的那份
    /// 与提交进仓库的那份是同一份」，复制会制造第二个可能过期的副本。
    /// </remarks>
    private static string SchemaRoot
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);

            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mateos.slnx")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                throw new InvalidOperationException("找不到仓库根目录（未发现 mateos.slnx）");
            }

            return Path.Combine(dir.FullName, "contracts", "schemas");
        }
    }
}
