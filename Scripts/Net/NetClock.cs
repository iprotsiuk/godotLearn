// Scripts/Net/NetClock.cs
namespace NetRunnerSlice.Net;

public sealed class NetClock
{
    private readonly int _serverTickRate;
    private long _syncLocalUsec;
    private int _syncServerTick;
    private bool _initialized;

    public uint LastServerTick { get; private set; }

    public NetClock(int serverTickRate)
    {
        _serverTickRate = serverTickRate > 0 ? serverTickRate : 1;
    }

    public uint GetEstimatedServerTick(long localUsec)
    {
        if (!_initialized)
        {
            return LastServerTick;
        }

        long deltaUsec = localUsec - _syncLocalUsec;
        if (deltaUsec < 0)
        {
            deltaUsec = 0;
        }

        long elapsedTicks = (deltaUsec * _serverTickRate) / 1_000_000L;
        long estimated = _syncServerTick + elapsedTicks;
        if (estimated < 0)
        {
            return 0;
        }

        return (uint)estimated;
    }

    public void ObserveServerTick(uint serverTick, long localUsec)
    {
        LastServerTick = serverTick;
        if (!_initialized)
        {
            _syncServerTick = (int)serverTick;
            _syncLocalUsec = localUsec;
            _initialized = true;
            return;
        }

        int estimatedNow = (int)GetEstimatedServerTick(localUsec);
        int errorTicks = (int)serverTick - estimatedNow;
        int correctionTicks = 0;
        if (errorTicks > 0)
        {
            correctionTicks = errorTicks > 1 ? 1 : errorTicks;
        }
        _syncServerTick = estimatedNow + correctionTicks;
        _syncLocalUsec = localUsec;
    }

    public void NudgeTowardServerTick(uint serverTick, long localUsec, int maxStepTicks = 1)
    {
        if (!_initialized)
        {
            ForceResync(serverTick, localUsec);
            return;
        }

        LastServerTick = serverTick;
        int estimatedNow = (int)GetEstimatedServerTick(localUsec);
        int errorTicks = (int)serverTick - estimatedNow;
        if (errorTicks == 0)
        {
            _syncServerTick = estimatedNow;
            _syncLocalUsec = localUsec;
            return;
        }

        int step = System.Math.Clamp(errorTicks, -System.Math.Max(1, maxStepTicks), System.Math.Max(1, maxStepTicks));
        _syncServerTick = estimatedNow + step;
        _syncLocalUsec = localUsec;
    }

    public void ForceResync(uint serverTick, long localUsec)
    {
        LastServerTick = serverTick;
        _syncServerTick = (int)serverTick;
        _syncLocalUsec = localUsec;
        _initialized = true;
    }
}
