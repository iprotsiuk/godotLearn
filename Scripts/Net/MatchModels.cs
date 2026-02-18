using NetRunnerSlice.GameModes;

namespace NetRunnerSlice.Net;

public enum MatchPhase : byte
{
    Running = 1,
    RestartCountdown = 2
}

public struct MatchConfig
{
    public GameModeId ModeId;
    public int RoundTimeSec;
}

public struct MatchState
{
    public int RoundIndex;
    public MatchPhase Phase;
    public uint PhaseEndTick;
}

public struct TagState
{
    public int RoundIndex;
    public int ItPeerId;
    public uint ItCooldownEndTick;
}
