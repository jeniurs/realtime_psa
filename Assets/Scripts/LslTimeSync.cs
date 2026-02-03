using System;
using System.Diagnostics;

public static class TimeSync
{
    private static readonly long _baseUtcMs;
    private static readonly long _baseTicks;
    private static readonly double _ticksToMs;

    static TimeSync()
    {
        _baseUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _baseTicks = Stopwatch.GetTimestamp();
        _ticksToMs = 1000.0 / Stopwatch.Frequency;
    }

    // 단조 증가 + UTC 앵커 (시스템 시간 점프 영향 최소화)
    public static long UtcNowMs()
    {
        long dt = Stopwatch.GetTimestamp() - _baseTicks;
        return _baseUtcMs + (long)(dt * _ticksToMs);
    }

    public static string FileStampUtc(long utcMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'");
}

// ===== 세션(동시 측정) 기준 시각/ID 공유 =====
public static class CaptureSession
{
    private static readonly object _lock = new object();

    public static string SessionId { get; private set; } = "";
    public static long StartUtcMs { get; private set; } = 0;
    public static bool Active => StartUtcMs > 0;

    // (선택) UI/로깅 연동용
    public static event Action<string, long> OnBegin;
    public static event Action OnEnd;

    public static void Begin(string sessionId, long startUtcMs)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || startUtcMs <= 0) return;

        lock (_lock)
        {
            SessionId = sessionId.Trim();
            StartUtcMs = startUtcMs;
        }
        OnBegin?.Invoke(SessionId, StartUtcMs);
    }

    public static void End()
    {
        lock (_lock)
        {
            SessionId = "";
            StartUtcMs = 0;
        }
        OnEnd?.Invoke();
    }
}
