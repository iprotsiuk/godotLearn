using System;
using System.Collections.Generic;
using Godot;
using NetRunnerSlice.GameModes;
using NetRunnerSlice.Net;
using NetRunnerSlice.Player;
using NetRunnerSlice.UI.Hud;

namespace NetRunnerSlice.Match;

public sealed class MatchManager
{
    private const int RestartCountdownSec = 5;

    private readonly GameModeRegistry _modeRegistry = GameModeRegistry.CreateDefault();
    private IGameMode? _activeMode;
    private GameModeId _activeModeId = GameModeId.FreeRun;

    private NetSession? _session;
    private Hud? _hud;
    private bool _serverActive;
    private bool _tagRestartRequested;
    private bool _clientSeenTagState;
    private int _clientSpectatorRoundIndex = -1;

    private MatchConfig _lastClientConfig = new()
    {
        ModeId = GameModeId.FreeRun,
        RoundTimeSec = 120
    };

    private MatchState _lastClientState = new()
    {
        RoundIndex = 0,
        Phase = MatchPhase.Running,
        PhaseEndTick = 0
    };

    private TagState _lastClientTagState = new()
    {
        RoundIndex = 0,
        ItPeerId = -1,
        ItCooldownEndTick = 0
    };

    public int RoundIndex { get; private set; }
    public MatchPhase Phase { get; private set; } = MatchPhase.Running;
    public uint PhaseEndTick { get; private set; }
    public HashSet<int> RoundParticipants { get; } = new();
    public HashSet<int> SpectatorsUntilNextRound { get; } = new();

    public void AttachSession(NetSession session)
    {
        if (_session == session)
        {
            return;
        }

        DetachSession();
        _session = session;
        _lastClientConfig = SanitizeMatchConfig(session.CurrentMatchConfig);
        _lastClientState = SanitizeMatchState(session.CurrentMatchState);
        _lastClientTagState = SanitizeTagState(session.CurrentTagState);
        session.ServerCanPickupItem = ServerCanPickupItem;

        session.MatchConfigReceived += OnMatchConfigReceived;
        session.MatchStateReceived += OnMatchStateReceived;
        session.TagStateFullReceived += OnTagStateFullReceived;
        session.TagStateDeltaReceived += OnTagStateDeltaReceived;
        session.ServerPeerJoined += OnServerPeerJoined;
        session.ServerPeerLeft += OnServerPeerLeft;
        session.ServerPostSimulatePlayer += OnServerPostSimulatePlayer;
    }

    public void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MatchConfigReceived -= OnMatchConfigReceived;
        _session.MatchStateReceived -= OnMatchStateReceived;
        _session.TagStateFullReceived -= OnTagStateFullReceived;
        _session.TagStateDeltaReceived -= OnTagStateDeltaReceived;
        _session.ServerPeerJoined -= OnServerPeerJoined;
        _session.ServerPeerLeft -= OnServerPeerLeft;
        _session.ServerPostSimulatePlayer -= OnServerPostSimulatePlayer;
        _session.ServerCanPickupItem = null;

        _session = null;
        _serverActive = false;
        _tagRestartRequested = false;
        RoundParticipants.Clear();
        SpectatorsUntilNextRound.Clear();
        _clientSeenTagState = false;
        _clientSpectatorRoundIndex = -1;

        _activeMode?.Exit();
        _activeMode = null;
        _activeModeId = GameModeId.FreeRun;
    }

    public void SetHud(Hud? hud)
    {
        _hud = hud;
    }

    public void ResetServerAuthorityState()
    {
        _serverActive = false;
        _tagRestartRequested = false;
        RoundIndex = 0;
        Phase = MatchPhase.Running;
        PhaseEndTick = 0;
        RoundParticipants.Clear();
        SpectatorsUntilNextRound.Clear();
        _clientSeenTagState = false;
        _clientSpectatorRoundIndex = -1;
    }

    public void Tick(in SessionMetrics metrics)
    {
        if (_session is null)
        {
            return;
        }

        if (_session.IsServer)
        {
            TickServer(metrics.ServerSimTick);
        }
        else if (_serverActive)
        {
            _serverActive = false;
            RoundParticipants.Clear();
            SpectatorsUntilNextRound.Clear();
        }

        if (_session.IsClient)
        {
            TickClientHud(metrics);
        }
        else
        {
            ClearHudMatchStats();
        }
    }

    private void TickServer(uint nowTick)
    {
        if (_session is null)
        {
            return;
        }

        if (!_serverActive)
        {
            _serverActive = true;
            RoundParticipants.Clear();
            SpectatorsUntilNextRound.Clear();
            StartRound(nowTick);
            return;
        }

        if (_tagRestartRequested && _activeModeId == GameModeId.TagClassic)
        {
            _tagRestartRequested = false;
            StartRound(nowTick);
            return;
        }

        if (IsWaitingForTagPlayers())
        {
            if (IsTagMinimumPlayersMet())
            {
                StartRound(nowTick);
            }

            return;
        }

        if (nowTick < PhaseEndTick)
        {
            return;
        }

        if (Phase == MatchPhase.Running)
        {
            _activeMode?.ServerOnRoundEnd(this, _session);
            BeginRestartCountdown(nowTick);
            return;
        }

        StartRound(nowTick);
    }

    private void TickClientHud(in SessionMetrics metrics)
    {
        if (_hud is null || _session is null)
        {
            return;
        }

        MatchConfig config = _session.IsServer
            ? SanitizeMatchConfig(_session.CurrentMatchConfig)
            : _lastClientConfig;
        MatchState state = _session.IsServer
            ? SanitizeMatchState(_session.CurrentMatchState)
            : _lastClientState;

        ActivateMode(config.ModeId);

        uint estimatedTick = _session.IsServer
            ? metrics.ServerSimTick
            : metrics.ClientEstServerTick;
        uint remainingTicks = state.PhaseEndTick > estimatedTick ? state.PhaseEndTick - estimatedTick : 0;
        int tickRate = Mathf.Max(1, _session.TickRate);
        int remainingSec = Mathf.CeilToInt(remainingTicks / (float)tickRate);

        _hud.SetStat("Mode", config.ModeId.ToString());
        _hud.SetStat("Phase", PhaseToDisplay(state.Phase));
        _hud.SetStat("TimeRemainingSec", remainingSec.ToString());
        _hud.SetStat("RoundIndex", state.RoundIndex.ToString());

        if (config.ModeId == GameModeId.TagClassic)
        {
            TagState tagState = _session.IsServer
                ? SanitizeTagState(_session.CurrentTagState)
                : _lastClientTagState;
            int localPeerId = _session.LocalPeerId;
            string role = ResolveTagRole(state, tagState, localPeerId);

            _hud.SetStat("TagRole", role);
            _hud.SetStat("ItPeerId", tagState.ItPeerId.ToString());

            if (role == "IT")
            {
                uint cooldownTicks = tagState.ItCooldownEndTick > estimatedTick
                    ? tagState.ItCooldownEndTick - estimatedTick
                    : 0;
                int cooldownSec = Mathf.CeilToInt(cooldownTicks / (float)tickRate);
                _hud.SetStat("TagCooldownRemainingSec", cooldownSec.ToString());
            }
            else
            {
                _hud.SetStat("TagCooldownRemainingSec", "-");
            }
        }
        else
        {
            _hud.RemoveStat("TagRole");
            _hud.RemoveStat("ItPeerId");
            _hud.RemoveStat("TagCooldownRemainingSec");
        }
    }

    private void ClearHudMatchStats()
    {
        if (_hud is null)
        {
            return;
        }

        _hud.RemoveStat("Mode");
        _hud.RemoveStat("Phase");
        _hud.RemoveStat("TimeRemainingSec");
        _hud.RemoveStat("RoundIndex");
        _hud.RemoveStat("TagRole");
        _hud.RemoveStat("ItPeerId");
        _hud.RemoveStat("TagCooldownRemainingSec");
    }

    private void StartRound(uint nowTick)
    {
        if (_session is null)
        {
            return;
        }

        MatchConfig config = SanitizeMatchConfig(_session.CurrentMatchConfig);
        _session.CurrentMatchConfig = config;
        ActivateMode(config.ModeId);

        RoundParticipants.Clear();
        foreach (int peerId in _session.GetServerPeerIds())
        {
            RoundParticipants.Add(peerId);
        }

        SpectatorsUntilNextRound.Clear();
        if (config.ModeId == GameModeId.TagClassic && !IsTagMinimumPlayersMet())
        {
            EnterWaitingForTagPlayers();
            return;
        }

        RoundIndex++;
        Phase = MatchPhase.Running;
        int tickRate = Mathf.Max(1, _session.TickRate);
        uint durationTicks = (uint)Mathf.Max(1, config.RoundTimeSec * tickRate);
        PhaseEndTick = nowTick + durationTicks;

        PublishState();
        _session.ServerClearAllEquippedItems();
        _session.ServerResetAllPickups();
        _activeMode?.ServerOnRoundStart(this, _session);
    }

    private void EnterWaitingForTagPlayers()
    {
        if (_session is null)
        {
            return;
        }

        Phase = MatchPhase.Running;
        PhaseEndTick = 0;
        PublishState();
        _session.SetCurrentTagStateFull(new TagState
        {
            RoundIndex = RoundIndex,
            ItPeerId = -1,
            ItCooldownEndTick = 0
        }, broadcast: true);
    }

    private void BeginRestartCountdown(uint nowTick)
    {
        if (_session is null)
        {
            return;
        }

        Phase = MatchPhase.RestartCountdown;
        int tickRate = Mathf.Max(1, _session.TickRate);
        PhaseEndTick = nowTick + (uint)(RestartCountdownSec * tickRate);
        PublishState();
    }

    private void PublishState()
    {
        if (_session is null)
        {
            return;
        }

        MatchState state = new()
        {
            RoundIndex = RoundIndex,
            Phase = Phase,
            PhaseEndTick = PhaseEndTick
        };
        _session.SetCurrentMatchState(state, broadcast: true);
    }

    private void ActivateMode(GameModeId modeId)
    {
        if (_activeMode is not null && _activeModeId == modeId)
        {
            return;
        }

        IGameMode next = _modeRegistry.Resolve(modeId);
        _activeMode?.Exit();
        _activeMode = next;
        _activeModeId = modeId;
        _activeMode.Enter();
    }

    private void OnServerPeerJoined(int peerId)
    {
        if (!_serverActive || Phase != MatchPhase.Running || IsWaitingForTagPlayers())
        {
            return;
        }

        SpectatorsUntilNextRound.Add(peerId);
        RoundParticipants.Remove(peerId);
    }

    private void OnServerPeerLeft(int peerId)
    {
        RoundParticipants.Remove(peerId);
        SpectatorsUntilNextRound.Remove(peerId);

        if (_session is null || !_serverActive || _activeModeId != GameModeId.TagClassic)
        {
            return;
        }

        if (_session.CurrentTagState.ItPeerId == peerId)
        {
            _tagRestartRequested = true;
        }
    }

    private void OnServerPostSimulatePlayer(int peerId, PlayerCharacter serverCharacter, InputCommand cmd, uint tick)
    {
        if (_session is null || !_session.IsServer || !_serverActive)
        {
            return;
        }

        _activeMode?.ServerOnPostSimulatePlayer(this, _session, peerId, serverCharacter, cmd, tick);
    }

    private bool ServerCanPickupItem(int peerId)
    {
        if (_session is null || !_session.IsServer || !_serverActive)
        {
            return false;
        }

        if (Phase != MatchPhase.Running)
        {
            return false;
        }

        if (_activeModeId != GameModeId.TagClassic)
        {
            return true;
        }

        if (IsWaitingForTagPlayers())
        {
            return true;
        }

        if (SpectatorsUntilNextRound.Contains(peerId))
        {
            return false;
        }

        if (!RoundParticipants.Contains(peerId))
        {
            return false;
        }

        if (_session.CurrentTagState.ItPeerId == peerId)
        {
            return false;
        }

        return true;
    }

    private void OnMatchConfigReceived(MatchConfig config)
    {
        _lastClientConfig = SanitizeMatchConfig(config);
    }

    private void OnMatchStateReceived(MatchState state)
    {
        MatchState previous = _lastClientState;
        _lastClientState = SanitizeMatchState(state);

        if (_lastClientState.RoundIndex > previous.RoundIndex &&
            _clientSpectatorRoundIndex < _lastClientState.RoundIndex)
        {
            _clientSpectatorRoundIndex = -1;
        }
    }

    private void OnTagStateFullReceived(TagState state)
    {
        _lastClientTagState = SanitizeTagState(state);

        if (_session is not null)
        {
            _activeMode?.ClientOnTagState(this, _session, _lastClientTagState, isFull: true);

            bool firstTagState = !_clientSeenTagState;
            _clientSeenTagState = true;
            if (firstTagState &&
                !_session.IsServer &&
                _lastClientState.Phase == MatchPhase.Running &&
                _lastClientState.RoundIndex == _lastClientTagState.RoundIndex &&
                _session.LocalPeerId > 0 &&
                _session.LocalPeerId != _lastClientTagState.ItPeerId)
            {
                _clientSpectatorRoundIndex = _lastClientState.RoundIndex;
            }
        }
    }

    private void OnTagStateDeltaReceived(TagState state)
    {
        _lastClientTagState = SanitizeTagState(state);
        if (_session is not null)
        {
            _activeMode?.ClientOnTagState(this, _session, _lastClientTagState, isFull: false);
        }
    }

    private string ResolveTagRole(MatchState state, TagState tagState, int localPeerId)
    {
        if (localPeerId <= 0)
        {
            return "SPECTATOR";
        }

        if (tagState.ItPeerId <= 0)
        {
            return "SPECTATOR";
        }

        if (_session is not null && _session.IsServer)
        {
            if (state.Phase == MatchPhase.Running && SpectatorsUntilNextRound.Contains(localPeerId))
            {
                return "SPECTATOR";
            }

            if (tagState.ItPeerId == localPeerId)
            {
                return "IT";
            }

            return RoundParticipants.Contains(localPeerId) ? "RUNNER" : "SPECTATOR";
        }

        bool spectatorThisRound =
            state.Phase == MatchPhase.Running &&
            _clientSpectatorRoundIndex == state.RoundIndex;
        if (spectatorThisRound)
        {
            return "SPECTATOR";
        }

        return tagState.ItPeerId == localPeerId ? "IT" : "RUNNER";
    }

    private static MatchConfig SanitizeMatchConfig(in MatchConfig config)
    {
        GameModeId mode = Enum.IsDefined(typeof(GameModeId), config.ModeId)
            ? config.ModeId
            : GameModeId.FreeRun;
        return new MatchConfig
        {
            ModeId = mode,
            RoundTimeSec = Mathf.Clamp(config.RoundTimeSec, 30, 900)
        };
    }

    private static MatchState SanitizeMatchState(in MatchState state)
    {
        MatchPhase phase = Enum.IsDefined(typeof(MatchPhase), state.Phase)
            ? state.Phase
            : MatchPhase.Running;
        return new MatchState
        {
            RoundIndex = Mathf.Max(0, state.RoundIndex),
            Phase = phase,
            PhaseEndTick = state.PhaseEndTick
        };
    }

    private static TagState SanitizeTagState(in TagState state)
    {
        return new TagState
        {
            RoundIndex = Mathf.Max(0, state.RoundIndex),
            ItPeerId = state.ItPeerId,
            ItCooldownEndTick = state.ItCooldownEndTick
        };
    }

    private bool IsTagMinimumPlayersMet()
    {
        if (_session is null)
        {
            return false;
        }

        return _session.GetServerPeerIds().Length >= 2;
    }

    private bool IsWaitingForTagPlayers()
    {
        return _activeModeId == GameModeId.TagClassic &&
            Phase == MatchPhase.Running &&
            PhaseEndTick == 0;
    }

    private static string PhaseToDisplay(MatchPhase phase)
    {
        return phase switch
        {
            MatchPhase.RestartCountdown => "RestartCountdown",
            _ => "Running"
        };
    }
}
