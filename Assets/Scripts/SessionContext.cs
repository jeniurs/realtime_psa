using System;

public static class SessionContext
{
    private static readonly object _lock = new object();

    public static string SessionId { get; private set; } = "";
    public static long StartUtcMs { get; private set; } = 0;
    public static bool IsStarted { get; private set; } = false;

    public static (string sessionId, long startUtcMs, bool isNew) StartIfNeeded(string preferSessionId, long nowUtcMs)
    {
        lock (_lock)
        {
            if (!IsStarted)
            {
                SessionId = !string.IsNullOrWhiteSpace(preferSessionId)
                    ? preferSessionId.Trim()
                    : $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}".Substring(0, 24);

                StartUtcMs = nowUtcMs;
                IsStarted = true;
                return (SessionId, StartUtcMs, true);
            }
            return (SessionId, StartUtcMs, false);
        }
    }

    public static void EnsureSession(string preferSessionId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(SessionId))
                SessionId = !string.IsNullOrWhiteSpace(preferSessionId)
                    ? preferSessionId.Trim()
                    : $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}".Substring(0, 24);
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            SessionId = "";
            StartUtcMs = 0;
            IsStarted = false;
        }
    }
}
