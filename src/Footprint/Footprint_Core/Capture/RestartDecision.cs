namespace Footprint.Core.Capture;

public enum RestartRequestKind
{
    Automatic,
    Manual
}

public enum RestartDecisionKind
{
    AllowAutomatic,
    AllowManual,
    WaitForSafeIdle,
    WaitForRemoteCommand,
    DenyGenerationConsumed,
    DenyDisabled,
    DenyProfile,
    DenyMaintenance,
    DenyConcurrent,
    DenyCooldown,
    DenyPolicyInvalid,
    DenyRequestInvalid
}

public sealed record RestartDecisionContext(
    RestartPolicy Policy,
    RestartRequestKind RequestKind,
    bool IsProfileValid,
    bool IsMaintenanceLocked,
    bool IsRestartAlreadyRunning,
    bool IsForeground,
    TimeSpan LastInputAge,
    bool IsGenerationBudgetConsumed,
    bool HasVerifiedRemoteCommand,
    DateTimeOffset? CooldownUntilUtc,
    DateTimeOffset NowUtc);

public sealed record RestartDecision(RestartDecisionKind Kind, string Code, string MessageZh)
{
    public bool IsAllowed => Kind is RestartDecisionKind.AllowAutomatic or RestartDecisionKind.AllowManual;
}

public static class RestartDecisionEngine
{
    // Retained for API compatibility; AutoOnce no longer returns WaitForSafeIdle.
    public static readonly TimeSpan SafeIdleThreshold = TimeSpan.FromMinutes(5);

    public static RestartDecision Decide(RestartDecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsProfileValid) return Create(RestartDecisionKind.DenyProfile,
            "restart_deny_profile", "微信版本配置未通过校验，不能重启。");
        if (context.IsMaintenanceLocked) return Create(RestartDecisionKind.DenyMaintenance,
            "restart_deny_maintenance", "维护锁已生效，暂不重启微信。");
        if (context.IsRestartAlreadyRunning) return Create(RestartDecisionKind.DenyConcurrent,
            "restart_deny_concurrent", "微信重启已在执行中。");
        if (!Enum.IsDefined(context.Policy)) return Create(RestartDecisionKind.DenyPolicyInvalid,
            "restart_deny_policy_invalid", "重启策略状态无效。");
        if (!Enum.IsDefined(context.RequestKind)) return Create(RestartDecisionKind.DenyRequestInvalid,
            "restart_deny_request_invalid", "重启请求类型无效。");
        if (context.Policy == RestartPolicy.Disabled) return Create(RestartDecisionKind.DenyDisabled,
            "restart_deny_disabled", "当前设备已禁用微信重启。");

        if (context.Policy == RestartPolicy.RemoteOnly &&
            (context.RequestKind != RestartRequestKind.Manual || !context.HasVerifiedRemoteCommand))
            return Create(RestartDecisionKind.WaitForRemoteCommand,
                "restart_wait_remote_command", "仅允许经验证的远程重启命令。");

        if (context.RequestKind == RestartRequestKind.Manual &&
            context.CooldownUntilUtc is { } cooldownUntilUtc && cooldownUntilUtc > context.NowUtc)
            return Create(RestartDecisionKind.DenyCooldown,
                "restart_deny_cooldown", "微信重启仍处于冷却期。");
        if (context.IsGenerationBudgetConsumed) return Create(RestartDecisionKind.DenyGenerationConsumed,
            "restart_deny_generation_consumed", "当前采集代际的重启预算已消耗。");

        if (context.RequestKind == RestartRequestKind.Manual) return Create(RestartDecisionKind.AllowManual,
            "restart_allow_manual", "手动重启条件已满足。");

        if (context.Policy == RestartPolicy.AutoOnce)
            return Create(RestartDecisionKind.AllowAutomatic,
                "restart_allow_automatic", "自动重启条件已满足。");

        return Create(RestartDecisionKind.WaitForRemoteCommand,
            "restart_wait_remote_command", "仅允许经验证的远程重启命令。");
    }

    private static RestartDecision Create(RestartDecisionKind kind, string code, string messageZh) =>
        new(kind, code, messageZh);
}
