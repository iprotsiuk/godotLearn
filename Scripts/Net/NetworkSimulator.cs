// Scripts/Net/NetworkSimulator.cs
using Godot;

namespace NetRunnerSlice.Net;

public sealed class NetworkSimulator
{
    private readonly System.Collections.Generic.List<PendingPacket> _pending = new(256);
    private readonly System.Random _rng;
    private readonly SendDelegate _sendDelegate;

    private int _latencyMs;
    private int _jitterMs;
    private float _lossPercent;

    public bool Enabled { get; private set; }

    public int LatencyMs => _latencyMs;

    public int JitterMs => _jitterMs;

    public float LossPercent => _lossPercent;

    public delegate void SendDelegate(int targetPeer, int channel, MultiplayerPeer.TransferModeEnum mode, byte[] packet);

    public NetworkSimulator(int seed, SendDelegate sendDelegate)
    {
        _rng = new System.Random(seed);
        _sendDelegate = sendDelegate;
    }

    public void Configure(bool enabled, int latencyMs, int jitterMs, float lossPercent)
    {
        Enabled = enabled;
        _latencyMs = Mathf.Max(0, latencyMs);
        _jitterMs = Mathf.Max(0, jitterMs);
        _lossPercent = Mathf.Clamp(lossPercent, 0.0f, 100.0f);
    }

    public void EnqueueSend(
        double nowSec,
        int targetPeer,
        int channel,
        MultiplayerPeer.TransferModeEnum mode,
        byte[] packet)
    {
        if (!Enabled)
        {
            _sendDelegate(targetPeer, channel, mode, packet);
            return;
        }

        bool lossAllowed = mode != MultiplayerPeer.TransferModeEnum.Reliable && channel != NetChannels.Control;
        if (lossAllowed && (_rng.NextDouble() * 100.0) < _lossPercent)
        {
            return;
        }

        int jitter = _jitterMs == 0 ? 0 : _rng.Next(-_jitterMs, _jitterMs + 1);
        double delaySec = (_latencyMs + jitter) / 1000.0;
        if (delaySec < 0.0)
        {
            delaySec = 0.0;
        }

        byte[] copy = new byte[packet.Length];
        System.Buffer.BlockCopy(packet, 0, copy, 0, packet.Length);

        _pending.Add(new PendingPacket
        {
            SendAtSec = nowSec + delaySec,
            TargetPeer = targetPeer,
            Channel = channel,
            Mode = mode,
            Packet = copy
        });
    }

    public void Flush(double nowSec)
    {
        for (int i = 0; i < _pending.Count;)
        {
            PendingPacket pending = _pending[i];
            if (pending.SendAtSec <= nowSec)
            {
                _sendDelegate(pending.TargetPeer, pending.Channel, pending.Mode, pending.Packet);
                int last = _pending.Count - 1;
                _pending[i] = _pending[last];
                _pending.RemoveAt(last);
                continue;
            }

            i++;
        }
    }

    private struct PendingPacket
    {
        public double SendAtSec;
        public int TargetPeer;
        public int Channel;
        public MultiplayerPeer.TransferModeEnum Mode;
        public byte[] Packet;
    }
}
