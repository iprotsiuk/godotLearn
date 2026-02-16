// Scripts/Net/ServerInputBuffer.cs
namespace NetRunnerSlice.Net;

public sealed class ServerInputBuffer
{
    private const int Capacity = 512;

    private readonly InputCommand[] _commands = new InputCommand[Capacity];
    private readonly bool[] _valid = new bool[Capacity];

    public void Clear()
    {
        System.Array.Fill(_valid, false);
    }

    public void Store(in InputCommand command)
    {
#if DEBUG
        bool hadBefore = TryGetBufferedTickRange(out _, out uint beforeMaxTick);
#endif
        int idx = (int)(command.InputTick % Capacity);

        if (_valid[idx] && _commands[idx].InputTick > command.InputTick)
        {
            return;
        }

        if (_valid[idx] && _commands[idx].InputTick == command.InputTick && _commands[idx].Seq >= command.Seq)
        {
            return;
        }

        _commands[idx] = command;
        _valid[idx] = true;
#if DEBUG
        if (hadBefore && TryGetBufferedTickRange(out _, out uint afterMaxTick))
        {
            System.Diagnostics.Debug.Assert(
                afterMaxTick >= beforeMaxTick,
                "ServerInputBuffer max buffered tick regressed after Store.");
        }
#endif
    }

    public bool TryTake(uint inputTick, out InputCommand command)
    {
        int idx = (int)(inputTick % Capacity);
        if (_valid[idx] && _commands[idx].InputTick == inputTick)
        {
            command = _commands[idx];
            _valid[idx] = false;
            return true;
        }

        command = default;
        return false;
    }

    public bool TryGetBufferedTickRange(out uint minTick, out uint maxTick)
    {
        return TryGetBufferedTickRange(0, out minTick, out maxTick);
    }

    public bool TryGetBufferedTickRange(uint minInputTickInclusive, out uint minTick, out uint maxTick)
    {
        bool found = false;
        minTick = 0;
        maxTick = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_valid[i])
            {
                continue;
            }

            uint tick = _commands[i].InputTick;
            if (tick < minInputTickInclusive)
            {
                continue;
            }

            if (!found)
            {
                minTick = tick;
                maxTick = tick;
                found = true;
                continue;
            }

            if (tick < minTick)
            {
                minTick = tick;
            }

            if (tick > maxTick)
            {
                maxTick = tick;
            }
        }

        return found;
    }

    public void PruneOlderThan(uint minInputTickInclusive)
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (_valid[i] && _commands[i].InputTick < minInputTickInclusive)
            {
                _valid[i] = false;
            }
        }

#if DEBUG
        for (int i = 0; i < Capacity; i++)
        {
            if (_valid[i])
            {
                System.Diagnostics.Debug.Assert(
                    _commands[i].InputTick >= minInputTickInclusive,
                    "ServerInputBuffer contains stale tick after prune.");
            }
        }
#endif
    }
}
