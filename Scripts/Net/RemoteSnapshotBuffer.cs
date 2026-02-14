// Scripts/Net/RemoteSnapshotBuffer.cs
using Godot;

namespace NetRunnerSlice.Net;

public sealed class RemoteSnapshotBuffer
{
    private const int Capacity = 64;

    private readonly double[] _times = new double[Capacity];
    private readonly RemoteSample[] _samples = new RemoteSample[Capacity];
    private int _count;

    public void Add(double serverTime, in RemoteSample sample)
    {
        if (_count == 0)
        {
            _times[0] = serverTime;
            _samples[0] = sample;
            _count = 1;
            return;
        }

        if (serverTime >= _times[_count - 1])
        {
            if (_count == Capacity)
            {
                ShiftLeft(1);
                _count--;
            }

            _times[_count] = serverTime;
            _samples[_count] = sample;
            _count++;
            return;
        }

        int insertAt = 0;
        while (insertAt < _count && _times[insertAt] < serverTime)
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
            _times[i] = _times[i - 1];
            _samples[i] = _samples[i - 1];
        }

        _times[insertAt] = serverTime;
        _samples[insertAt] = sample;
        _count++;
    }

    public bool TrySample(double renderTime, double maxExtrapolation, out RemoteSample sampled)
    {
        sampled = default;
        if (_count == 0)
        {
            return false;
        }

        if (_count == 1 || renderTime <= _times[0])
        {
            sampled = _samples[0];
            return true;
        }

        for (int i = 0; i < (_count - 1); i++)
        {
            double aTime = _times[i];
            double bTime = _times[i + 1];
            if (renderTime <= bTime)
            {
                float t = (float)((renderTime - aTime) / (bTime - aTime));
                sampled = Lerp(_samples[i], _samples[i + 1], t);
                return true;
            }
        }

        RemoteSample newest = _samples[_count - 1];
        double dt = renderTime - _times[_count - 1];
        if (dt > maxExtrapolation)
        {
            dt = maxExtrapolation;
        }

        newest.Pos += newest.Vel * (float)dt;
        sampled = newest;
        return true;
    }

    private static RemoteSample Lerp(in RemoteSample a, in RemoteSample b, float t)
    {
        return new RemoteSample
        {
            Pos = a.Pos.Lerp(b.Pos, t),
            Vel = a.Vel.Lerp(b.Vel, t),
            Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t),
            Pitch = Mathf.Lerp(a.Pitch, b.Pitch, t),
            Grounded = t < 0.5f ? a.Grounded : b.Grounded
        };
    }

    private void ShiftLeft(int by)
    {
        int remaining = _count - by;
        for (int i = 0; i < remaining; i++)
        {
            _times[i] = _times[i + by];
            _samples[i] = _samples[i + by];
        }
    }
}
