// Scripts/Net/ServerInputBuffer.cs
namespace NetRunnerSlice.Net;

public sealed class ServerInputBuffer
{
    private const int Capacity = 512;

    private readonly InputCommand[] _commands = new InputCommand[Capacity];
    private readonly bool[] _valid = new bool[Capacity];

    public void Push(in InputCommand command)
    {
        int idx = (int)(command.Seq % Capacity);
        _commands[idx] = command;
        _valid[idx] = true;
    }

    public bool TryTakeExact(uint seq, out InputCommand command)
    {
        int idx = (int)(seq % Capacity);
        if (_valid[idx] && _commands[idx].Seq == seq)
        {
            command = _commands[idx];
            _valid[idx] = false;
            return true;
        }

        command = default;
        return false;
    }

    public bool TryTakeLowestAfter(uint seqExclusive, out InputCommand command, out uint seq)
    {
        seq = uint.MaxValue;
        int bestIdx = -1;

        for (int i = 0; i < Capacity; i++)
        {
            if (!_valid[i])
            {
                continue;
            }

            uint candidate = _commands[i].Seq;
            if (candidate > seqExclusive && candidate < seq)
            {
                seq = candidate;
                bestIdx = i;
            }
        }

        if (bestIdx >= 0)
        {
            command = _commands[bestIdx];
            _valid[bestIdx] = false;
            return true;
        }

        command = default;
        seq = 0;
        return false;
    }
}
