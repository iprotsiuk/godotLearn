using System.Collections.Generic;
using Godot;

namespace NetRunnerSlice.UI.Hud;

public partial class Hud : Control
{
    private Label? _healthValue;
    private Label? _ammoValue;
    private VBoxContainer? _extrasRows;
    private readonly Dictionary<string, Label> _extraValues = new();

    public override void _Ready()
    {
        _healthValue = GetNode<Label>("Root/Panel/Margin/Rows/HealthRow/Value");
        _ammoValue = GetNode<Label>("Root/Panel/Margin/Rows/AmmoRow/Value");
        _extrasRows = GetNode<VBoxContainer>("Root/Panel/Margin/Rows/Extras");

        SetHealth(100, 100);
        SetAmmo(30, 90);
    }

    public void SetHealth(int current, int max)
    {
        if (_healthValue is null)
        {
            return;
        }

        int safeMax = max <= 0 ? 1 : max;
        int safeCurrent = Mathf.Clamp(current, 0, safeMax);
        _healthValue.Text = $"{safeCurrent} / {safeMax}";
    }

    public void SetAmmo(int current, int reserve = -1)
    {
        if (_ammoValue is null)
        {
            return;
        }

        int safeCurrent = Mathf.Max(0, current);
        _ammoValue.Text = reserve < 0
            ? $"{safeCurrent} / INF"
            : $"{safeCurrent} / {Mathf.Max(0, reserve)}";
    }

    public void SetStat(string key, string value)
    {
        if (_extrasRows is null || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string normalizedKey = key.Trim();
        if (!_extraValues.TryGetValue(normalizedKey, out Label? valueLabel))
        {
            HBoxContainer row = new();

            Label keyLabel = new()
            {
                Text = $"{normalizedKey}:",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            row.AddChild(keyLabel);

            valueLabel = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Right
            };
            row.AddChild(valueLabel);

            _extrasRows.AddChild(row);
            _extraValues[normalizedKey] = valueLabel;
        }

        valueLabel.Text = value;
    }

    public void RemoveStat(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string normalizedKey = key.Trim();
        if (!_extraValues.TryGetValue(normalizedKey, out Label? valueLabel))
        {
            return;
        }

        Node? row = valueLabel.GetParent();
        row?.QueueFree();
        _extraValues.Remove(normalizedKey);
    }

    public void ClearStats()
    {
        foreach (Label valueLabel in _extraValues.Values)
        {
            valueLabel.GetParent()?.QueueFree();
        }

        _extraValues.Clear();
    }
}
