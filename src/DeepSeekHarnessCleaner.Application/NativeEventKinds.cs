namespace DeepSeekHarnessCleaner.Application;

/// <summary>本机已观察并与上游事件目录核对的非正文事件；不展开其内部配置或请求载荷。</summary>
public static class NativeEventKinds
{
    public static readonly IReadOnlySet<string> Metadata = new HashSet<string>(StringComparer.Ordinal)
    {
        "request/header", "request/context", "todo/write", "session/title", "session/title-llm-request",
        "session/end-seed", "session-log-deepseek/delivery-accepted", "step/start", "step/end", "turn/start", "turn/end",
        "assistant/attempt", "assistant/chunk", "text-chunks", "reasoning-chunks", "tool-call-chunks",
        "model/selection", "agent-preset/selected", "agent/inbox/spliced", "approval/asked", "approval/decided",
        "approval/policy", "permission/preset", "sandbox/mode", "command/run", "command/done",
        "compaction/start", "compaction/end", "compaction/prune", "compaction/summary", "goal/change",
        "job/registered", "job/settled", "llm/retry", "llm/retry-started", "subagent/catalog",
        "subagent/descriptor", "subagent/model-selection-policy", "working-directory/change", "deliverables/presented",
        "web/deepseek-search-llm-request", "workspace/changes",
    };
}
