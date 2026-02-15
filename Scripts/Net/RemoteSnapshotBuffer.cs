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

    public bool TrySample(double renderTime, double maxExtrapolation, bool useHermiteInterpolation, out RemoteSample sampled)
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
                float segmentDuration = (float)(bTime - aTime);
                sampled = Interpolate(_samples[i], _samples[i + 1], t, segmentDuration, useHermiteInterpolation);
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
            _times[i] = _times[i + by];
            _samples[i] = _samples[i + by];
        }
    }
}
