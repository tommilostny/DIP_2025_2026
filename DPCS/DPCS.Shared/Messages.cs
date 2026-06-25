namespace DPCS.Shared;

// =================== AGENT WORKER ACTOR MESSAGES ===================

public sealed record StartLoop;
public sealed record StopWork;
public sealed record PrefetchWork;
public sealed record ProcessWork;
public sealed record HeartbeatTick;
