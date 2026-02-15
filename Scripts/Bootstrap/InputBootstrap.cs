// Scripts/Bootstrap/InputBootstrap.cs
using Godot;

namespace NetRunnerSlice.Bootstrap;

public static class InputBootstrap
{
    public static void EnsureActions()
    {
        EnsureKeyAction("move_forward", Key.W);
        EnsureKeyAction("move_back", Key.S);
        EnsureKeyAction("move_left", Key.A);
        EnsureKeyAction("move_right", Key.D);
        EnsureKeyAction("jump", Key.Space);
        EnsureKeyAction("quit", Key.Escape);
        EnsureKeyAction("toggle_debug", Key.F1);
        EnsureMouseAction("fire", MouseButton.Left);
    }

    private static void EnsureKeyAction(string action, Key key)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }

        foreach (InputEvent existing in InputMap.ActionGetEvents(action))
        {
            if (existing is InputEventKey keyEvent && keyEvent.Keycode == key)
            {
                return;
            }
        }

        InputEventKey eventKey = new()
        {
            Keycode = key,
            PhysicalKeycode = key,
            Pressed = false
        };
        InputMap.ActionAddEvent(action, eventKey);
    }

    private static void EnsureMouseAction(string action, MouseButton button)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }

        foreach (InputEvent existing in InputMap.ActionGetEvents(action))
        {
            if (existing is InputEventMouseButton mouseEvent && mouseEvent.ButtonIndex == button)
            {
                return;
            }
        }

        InputEventMouseButton eventMouse = new()
        {
            ButtonIndex = button,
            Pressed = false
        };
        InputMap.ActionAddEvent(action, eventMouse);
    }
}
