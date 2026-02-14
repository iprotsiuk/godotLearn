// Scripts/Net/NetClock.cs
namespace NetRunnerSlice.Net;

public sealed class NetClock
{
    private readonly double _serverTickIntervalSec;
    private double _offsetSec;
    private bool _initialized;

    public uint LastServerTick { get; private set; }

    public NetClock(int serverTickRate)
    {
        _serverTickIntervalSec = 1.0 / serverTickRate;
    }

    public double GetEstimatedServerTime(double localTimeSec)
    {
        return localTimeSec + _offsetSec;
    }

    public void ObserveServerTick(uint serverTick, double localTimeSec, float rttMs)
    {
        LastServerTick = serverTick;
        double serverTimeSec = serverTick * _serverTickIntervalSec;
        double oneWaySec = (rttMs * 0.5) / 1000.0;
        double sampleOffset = (serverTimeSec + oneWaySec) - localTimeSec;

        if (!_initialized)
        {
            _offsetSec = sampleOffset;
            _initialized = true;
            return;
        }

        // Slew instead of hard-jumping.
        const double blend = 0.1;
        _offsetSec += (sampleOffset - _offsetSec) * blend;
    }
}
