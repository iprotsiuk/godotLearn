// Scripts/Debug/DebugOverlay.cs
using Godot;
using NetRunnerSlice.Net;

namespace NetRunnerSlice.Debug;

public partial class DebugOverlay : CanvasLayer
{
    [Signal]
    public delegate void NetSimChangedEventHandler(bool enabled, int latencyMs, int jitterMs, float lossPercent);

    private Panel? _panel;
    private Label? _titleLabel;
    private Label? _statsLabel;

    private CheckBox? _simEnabled;
    private SpinBox? _latencyMs;
    private SpinBox? _jitterMs;
    private SpinBox? _lossPercent;
    private string _profileName = "DEFAULT";

    private bool _visible = true;
    private double _nextStatsRefreshAtSec;

    public override void _Ready()
    {
        Layer = 20;

        MarginContainer root = new()
        {
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        root.AddThemeConstantOverride("margin_left", 8);
        root.AddThemeConstantOverride("margin_top", 8);
        AddChild(root);

        _panel = new Panel
        {
            CustomMinimumSize = new Vector2(540.0f, 380.0f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        root.AddChild(_panel);

        VBoxContainer vbox = new()
        {
            Position = new Vector2(10.0f, 10.0f),
            Size = new Vector2(520.0f, 360.0f)
        };
        _panel.AddChild(vbox);

        _titleLabel = new Label { Text = "Debug (F1 to toggle) | FPS: 0.0" };
        vbox.AddChild(_titleLabel);

        _statsLabel = new Label
        {
            CustomMinimumSize = new Vector2(510.0f, 230.0f),
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.Off
        };
        vbox.AddChild(_statsLabel);

        HSeparator separator = new();
        vbox.AddChild(separator);

        GridContainer grid = new() { Columns = 2 };
        vbox.AddChild(grid);

        grid.AddChild(new Label { Text = "Net Sim Enabled" });
        _simEnabled = new CheckBox();
        grid.AddChild(_simEnabled);

        grid.AddChild(new Label { Text = "Latency (ms)" });
        _latencyMs = CreateSpinBox(0.0, 2000.0, 1.0, false);
        grid.AddChild(_latencyMs);

        grid.AddChild(new Label { Text = "Jitter (ms)" });
        _jitterMs = CreateSpinBox(0.0, 1000.0, 1.0, false);
        grid.AddChild(_jitterMs);

        grid.AddChild(new Label { Text = "Loss (%)" });
        _lossPercent = CreateSpinBox(0.0, 100.0, 0.1, true);
        grid.AddChild(_lossPercent);

        Button applyButton = new() { Text = "Apply Net Sim" };
        applyButton.Pressed += OnApplyPressed;
        vbox.AddChild(applyButton);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("toggle_debug"))
        {
            _visible = !_visible;
            if (_panel is not null)
            {
                _panel.Visible = _visible;
            }
        }
    }

    public void SetSimulationControls(bool enabled, int latencyMs, int jitterMs, float lossPercent)
    {
        if (_simEnabled is null || _latencyMs is null || _jitterMs is null || _lossPercent is null)
        {
            return;
        }

        _simEnabled.ButtonPressed = enabled;
        _latencyMs.Value = latencyMs;
        _jitterMs.Value = jitterMs;
        _lossPercent.Value = lossPercent;
    }

    public void SetProfileName(string profileName)
    {
        _profileName = string.IsNullOrWhiteSpace(profileName) ? "DEFAULT" : profileName.ToUpperInvariant();
    }

    public void Update(SessionMetrics metrics, bool isServer, bool isClient)
    {
        if (_statsLabel is null)
        {
            return;
        }

        double nowSec = Time.GetTicksMsec() / 1000.0;
        if (nowSec < _nextStatsRefreshAtSec)
        {
            return;
        }

        _nextStatsRefreshAtSec = nowSec + 0.1;
        if (_titleLabel is not null)
        {
            _titleLabel.Text = $"Debug (F1 to toggle) | FPS: {metrics.FramesPerSecond:0.0}";
        }

        string rttText = metrics.RttMs < 0.0f ? "N/A" : $"{metrics.RttMs:0.0} ms";
        string jitterText = metrics.JitterMs < 0.0f ? "N/A" : $"{metrics.JitterMs:0.0} ms";
        string interpDelayText = metrics.DynamicInterpolationDelayMs < 0.0f
            ? "N/A"
            : $"{metrics.DynamicInterpolationDelayMs:0.0} ms";
        string sessionJitterText = metrics.SessionJitterEstimateMs < 0.0f
            ? "N/A"
            : $"{metrics.SessionJitterEstimateMs:0.0} ms";
        string snapshotAgeText = metrics.SnapshotAgeMs < 0.0f
            ? "N/A"
            : $"{metrics.SnapshotAgeMs:0.0} ms";
        string serverPeerRttText = metrics.ServerPeerRttMs < 0.0f ? "N/A" : $"{metrics.ServerPeerRttMs:0.0} ms";
        string serverPeerJitterText = metrics.ServerPeerJitterMs < 0.0f ? "N/A" : $"{metrics.ServerPeerJitterMs:0.0} ms";
        uint serverUsageTotal = metrics.ServerTicksUsedBufferedInput + metrics.ServerTicksUsedHoldLast + metrics.ServerTicksUsedNeutral;
        float bufferedPct = serverUsageTotal == 0
            ? 0.0f
            : (100.0f * metrics.ServerTicksUsedBufferedInput) / serverUsageTotal;

        _statsLabel.Text =
            $"Role: {(isServer ? "Server" : "")}{(isServer && isClient ? "/" : "")}{(isClient ? "Client" : "")}" +
            $"\nFPS: {metrics.FramesPerSecond:0.0}" +
            $"\nServer Sim Tick: {metrics.ServerSimTick}" +
            $"\nClient Est Server Tick: {metrics.ClientEstServerTick}" +
            $"\nTick Error: {metrics.TickErrorTicks}" +
            $"\nClient Send Tick: {metrics.ClientSendTick}" +
            $"\nRTT: {rttText}" +
            $"\nJitter: {jitterText}" +
            $"\nGlobal Interp Delay: {interpDelayText}" +
            $"\nSession Jitter Estimate: {sessionJitterText}" +
            $"\nSnapshot Age: {snapshotAgeText}" +
            $"\nLast Acked Seq: {metrics.LastAckedInput}" +
            $"\nPending Inputs Count/Cap: {metrics.PendingInputCount}/{metrics.PendingInputsCap}" +
            $"\nDropFuture Rate (5s): {metrics.DropFutureRatePerSec:0.00}/s" +
            $"\nRESYNC Triggered/Count: {(metrics.ResyncTriggered ? "Yes" : "No")} / {metrics.ResyncCount}" +
            $"\nJump Repeat Left: {metrics.JumpRepeatRemaining}" +
            $"\nCorrections/sec: {metrics.CorrectionsPerSec:0.00}" +
            $"\nNet Correction Magnitude (3D m): {metrics.Corr3D:0.000}" +
            $"\nLast Correction XZ/Y/3D (m): {metrics.CorrXZ:0.000} / {metrics.CorrY:0.000} / {metrics.Corr3D:0.000}" +
            $"\nRender/View/Camera Offset (mm): " +
            $"{metrics.RenderCorrectionOffset.X * 1000.0f:0.0},{metrics.RenderCorrectionOffset.Y * 1000.0f:0.0},{metrics.RenderCorrectionOffset.Z * 1000.0f:0.0} / " +
            $"{metrics.ViewCorrectionOffset.X * 1000.0f:0.0},{metrics.ViewCorrectionOffset.Y * 1000.0f:0.0},{metrics.ViewCorrectionOffset.Z * 1000.0f:0.0} / " +
            $"{metrics.CameraCorrectionOffset.X * 1000.0f:0.0},{metrics.CameraCorrectionOffset.Y * 1000.0f:0.0},{metrics.CameraCorrectionOffset.Z * 1000.0f:0.0}" +
            $"\nLocal Grounded: {(metrics.LocalGrounded ? "Yes" : "No")}" +
            $"\nMoveSpeed/GroundAccel: {metrics.MoveSpeed:0.###} / {metrics.GroundAcceleration:0.###}" +
            $"\nServerInputDelayTicks: {metrics.ServerInputDelayTicks}" +
            $"\nServerDiag Drops old/future: {metrics.ServerDroppedOldInputCount} / {metrics.ServerDroppedFutureInputCount}" +
            $"\nServerDiag Usage buffered/hold/neutral: {metrics.ServerTicksUsedBufferedInput} / {metrics.ServerTicksUsedHoldLast} / {metrics.ServerTicksUsedNeutral} ({bufferedPct:0.0}% buffered)" +
            $"\nServerDiag Missing streak cur/max: {metrics.ServerMissingInputStreakCurrent} / {metrics.ServerMissingInputStreakMax}" +
            $"\nServerDiag EffectiveDelayTicks: {metrics.ServerEffectiveDelayTicks}" +
            $"\nServerDiag RTT/Jitter: {serverPeerRttText} / {serverPeerJitterText}" +
            $"\nProfile: {_profileName}" +
            $"\nNetSim: {(metrics.NetworkSimulationEnabled ? "ON" : "OFF")} ({metrics.SimLatencyMs}ms/{metrics.SimJitterMs}ms/{metrics.SimLossPercent:0.0}% loss)";
    }

    private void OnApplyPressed()
    {
        if (_simEnabled is null || _latencyMs is null || _jitterMs is null || _lossPercent is null)
        {
            return;
        }

        GD.Print(
            $"DebugOverlay: Apply pressed (enabled={_simEnabled.ButtonPressed}, latency={(int)_latencyMs.Value}, jitter={(int)_jitterMs.Value}, loss={(float)_lossPercent.Value:0.0})");

        EmitSignal(
            SignalName.NetSimChanged,
            _simEnabled.ButtonPressed,
            (int)_latencyMs.Value,
            (int)_jitterMs.Value,
            (float)_lossPercent.Value);
    }

    private static SpinBox CreateSpinBox(double min, double max, double step, bool allowFraction)
    {
        SpinBox spin = new()
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Rounded = !allowFraction
        };
        spin.CustomMinimumSize = new Vector2(120.0f, 0.0f);
        return spin;
    }
}
