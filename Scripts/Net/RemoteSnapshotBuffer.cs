// Scripts/Net/RemoteSnapshotBuffer.cs
using Godot;

namespace NetRunnerSlice.Net;

public sealed class RemoteSnapshotBuffer
{
    private const int Capacity = 64;
    private const float ArrivalIntervalEwmaAlpha = 0.1f;
    private const float ArrivalJitterEwmaAlpha = 0.2f;

    private readonly uint[] _ticks = new uint[Capacity];
    private readonly RemoteSample[] _samples = new RemoteSample[Capacity];
    private int _count;
    private bool _hasArrivalSample;
    private double _lastArrivalSec;
    private double _arrivalIntervalEwmaSec;
    private float _arrivalJitterEwmaMs;

    public float ArrivalJitterEwmaMs => _arrivalJitterEwmaMs;

    public void Add(uint serverTick, in RemoteSample sample)
    {
        if (_count == 0)
        {
            _ticks[0] = serverTick;
            _samples[0] = sample;
            _count = 1;
            return;
        }

        if (serverTick >= _ticks[_count - 1])
        {
            if (_count == Capacity)
            {
                ShiftLeft(1);
                _count--;
            }

            _ticks[_count] = serverTick;
            _samples[_count] = sample;
            _count++;
            return;
        }

        int insertAt = 0;
        while (insertAt < _count && _ticks[insertAt] < serverTick)
        {
            insertAt++;
        }

        if (_count == Capacity)
        {
            ShiftLeft(1);
            _count--;
            insertAt = Mathf.Max(0, insertAt - 1);
        }

        for (int i = _count; i > insertAt; i--)
        {
            _ticks[i] = _ticks[i - 1];
            _samples[i] = _samples[i - 1];
        }

        _ticks[insertAt] = serverTick;
        _samples[insertAt] = sample;
        _count++;
    }

    public void ObserveArrival(double arrivalSec, double expectedDeltaSec)
    {
        if (!_hasArrivalSample)
        {
            _hasArrivalSample = true;
            _lastArrivalSec = arrivalSec;
            return;
        }

        double arrivalDeltaSec = arrivalSec - _lastArrivalSec;
        _lastArrivalSec = arrivalSec;
        if (arrivalDeltaSec <= 0.0)
        {
            return;
        }

        if (_arrivalIntervalEwmaSec <= 0.0)
        {
            _arrivalIntervalEwmaSec = arrivalDeltaSec;
        }
        else
        {
            _arrivalIntervalEwmaSec = Mathf.Lerp(
                (float)_arrivalIntervalEwmaSec,
                (float)arrivalDeltaSec,
                ArrivalIntervalEwmaAlpha);
        }

        double expectedSec = expectedDeltaSec > 0.0 ? expectedDeltaSec : _arrivalIntervalEwmaSec;
        float deltaFromExpectedMs = (float)(Mathf.Abs((float)(arrivalDeltaSec - expectedSec)) * 1000.0f);
        if (_arrivalJitterEwmaMs <= 0.001f)
        {
            _arrivalJitterEwmaMs = deltaFromExpectedMs;
        }
        else
        {
            _arrivalJitterEwmaMs = Mathf.Lerp(_arrivalJitterEwmaMs, deltaFromExpectedMs, ArrivalJitterEwmaAlpha);
        }
    }

    public bool TrySample(double renderTick, double maxExtrapolationTicks, bool useHermiteInterpolation, out RemoteSample sampled, out bool underflow)
    {
        sampled = default;
        underflow = false;
        if (_count == 0)
        {
            return false;
        }

        if (_count == 1 || renderTick <= _ticks[0])
        {
            sampled = _samples[0];
            if (_count == 1 && renderTick > _ticks[0])
            {
                underflow = true;
                double dt = renderTick - _ticks[0];
                if (dt > maxExtrapolationTicks)
                {
                    dt = maxExtrapolationTicks;
                }

                sampled.Pos += sampled.Vel * (float)dt;
            }
            return true;
        }

        for (int i = 0; i < (_count - 1); i++)
        {
            double aTick = _ticks[i];
            double bTick = _ticks[i + 1];
            if (renderTick <= bTick)
            {
                float t = (float)((renderTick - aTick) / (bTick - aTick));
                float segmentDuration = (float)(bTick - aTick);
                sampled = Interpolate(_samples[i], _samples[i + 1], t, segmentDuration, useHermiteInterpolation);
                return true;
            }
        }

        RemoteSample newest = _samples[_count - 1];
        double extrapTicks = renderTick - _ticks[_count - 1];
        underflow = true;
        if (extrapTicks > maxExtrapolationTicks)
        {
            extrapTicks = maxExtrapolationTicks;
        }

        newest.Pos += newest.Vel * (float)extrapTicks;
        sampled = newest;
        return true;
    }

    private static RemoteSample Interpolate(
        in RemoteSample a,
        in RemoteSample b,
        float t,
        float segmentDuration,
        bool useHermiteInterpolation)
    {
        Vector3 pos;
        if (useHermiteInterpolation && segmentDuration > 0.0001f)
        {
            pos = HermitePosition(a.Pos, a.Vel, b.Pos, b.Vel, t, segmentDuration);
        }
        else
        {
            pos = a.Pos.Lerp(b.Pos, t);
        }

        return new RemoteSample
        {
            Pos = pos,
            Vel = a.Vel.Lerp(b.Vel, t),
            Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t),
            Pitch = Mathf.Lerp(a.Pitch, b.Pitch, t),
            Grounded = t < 0.5f ? a.Grounded : b.Grounded
        };
    }

    private static Vector3 HermitePosition(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float t, float dt)
    {
        // Use endpoint velocities as tangents to preserve smooth diagonal motion between sparse snapshots.
        float t2 = t * t;
        float t3 = t2 * t;

        float h00 = (2.0f * t3) - (3.0f * t2) + 1.0f;
        float h10 = t3 - (2.0f * t2) + t;
        float h01 = (-2.0f * t3) + (3.0f * t2);
        float h11 = t3 - t2;

        return (h00 * p0) + (h10 * (v0 * dt)) + (h01 * p1) + (h11 * (v1 * dt));
    }

    private void ShiftLeft(int by)
    {
        int remaining = _count - by;
        for (int i = 0; i < remaining; i++)
        {
            _ticks[i] = _ticks[i + by];
            _samples[i] = _samples[i + by];
        }
    }
}
