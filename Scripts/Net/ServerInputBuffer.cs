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
        int idx = (int)(command.InputTick % Capacity);

        if (_valid[idx] && _commands[idx].InputTick == command.InputTick && _commands[idx].Seq >= command.Seq)
        {
            return;
        }

        _commands[idx] = command;
        _valid[idx] = true;
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
}
