// Scripts/Net/InputHistoryBuffer.cs
namespace NetRunnerSlice.Net;

public sealed class InputHistoryBuffer
{
    private const int Capacity = 1024;

    private readonly InputCommand[] _commands = new InputCommand[Capacity];
    private readonly bool[] _valid = new bool[Capacity];

    private uint _minSeq = 1;
    private uint _maxSeq;
    private int _count;

    public int Count => _count;

    public void Add(in InputCommand command)
    {
        int idx = (int)(command.Seq % Capacity);
        bool replacing = _valid[idx] && _commands[idx].Seq != command.Seq;

        if (!_valid[idx])
        {
            _count++;
        }

        _commands[idx] = command;
        _valid[idx] = true;

        if (replacing)
        {
            _minSeq = FindMinSeq();
        }

        if (command.Seq > _maxSeq)
        {
            _maxSeq = command.Seq;
        }

        if (_count > Capacity)
        {
            RemoveUpTo(command.Seq - Capacity);
        }

        if (_count == 1)
        {
            _minSeq = command.Seq;
        }
    }

    public bool TryGet(uint seq, out InputCommand command)
    {
        int idx = (int)(seq % Capacity);
        if (_valid[idx] && _commands[idx].Seq == seq)
        {
            command = _commands[idx];
            return true;
        }

        command = default;
        return false;
    }

    public void Clear()
    {
        System.Array.Fill(_valid, false);
        _minSeq = 1;
        _maxSeq = 0;
        _count = 0;
    }

    public void RemoveUpTo(uint seqInclusive)
    {
        if (_count == 0)
        {
            return;
        }

        uint start = _minSeq;
        uint end = seqInclusive < _maxSeq ? seqInclusive : _maxSeq;

        for (uint seq = start; seq <= end; seq++)
        {
            int idx = (int)(seq % Capacity);
            if (_valid[idx] && _commands[idx].Seq == seq)
            {
                _valid[idx] = false;
                _count--;
            }
        }

        _minSeq = _count > 0 ? FindMinSeq() : (seqInclusive + 1);
    }

    public int GetLatest(int maxCount, Span<InputCommand> output, uint newestSeq)
    {
        int count = 0;
        for (int i = 0; i < maxCount; i++)
        {
            if (newestSeq < (uint)i)
            {
                break;
            }

            uint seq = newestSeq - (uint)i;
            if (TryGet(seq, out InputCommand cmd))
            {
                output[count++] = cmd;
            }
        }

        return count;
    }

    private uint FindMinSeq()
    {
        uint min = uint.MaxValue;
        for (int i = 0; i < Capacity; i++)
        {
            if (_valid[i] && _commands[i].Seq < min)
            {
                min = _commands[i].Seq;
            }
        }

        return min == uint.MaxValue ? 1 : min;
    }
}
