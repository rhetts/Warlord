using System.Collections.Generic;
using Godot;

namespace Warlord.Game;

/// <summary>
/// Defines the character-mode control scheme as named Godot input actions and
/// persists any rebinding to user://keybindings.cfg. Actions are registered
/// entirely in code (via InputMap) rather than in project.godot, so this file
/// is the single source of truth for both the defaults and the display
/// labels the Settings view lists them under.
/// </summary>
public static class KeyBindings
{
    public sealed record ActionDef(string Name, string Label, InputEvent Default);

    public static readonly List<ActionDef> Actions = new()
    {
        new("cam_orbit_up", "Camera Up", KeyEvent(Key.Up)),
        new("cam_orbit_down", "Camera Down", KeyEvent(Key.Down)),
        new("cam_orbit_left", "Camera Left", KeyEvent(Key.Left)),
        new("cam_orbit_right", "Camera Right", KeyEvent(Key.Right)),
        new("move_forward", "Move Forward", KeyEvent(Key.S)),
        new("move_back", "Move Back", KeyEvent(Key.W)),
        new("move_left", "Turn Left", KeyEvent(Key.A)),
        new("move_right", "Turn Right", KeyEvent(Key.D)),
        new("swing_left_arm", "Swing Left Arm", MouseEvent(MouseButton.Left)),
        new("swing_right_arm", "Swing Right Arm", MouseEvent(MouseButton.Right)),
        new("jump", "Jump", KeyEvent(Key.Space)),
    };

    private const string ConfigPath = "user://keybindings.cfg";
    private const string Section = "bindings";

    private static InputEventKey KeyEvent(Key key) => new() { PhysicalKeycode = key };
    private static InputEventMouseButton MouseEvent(MouseButton button) => new() { ButtonIndex = button };

    /// <summary>Registers every action with its default binding, then applies any saved override. Safe to call more than once.</summary>
    public static void Initialize()
    {
        var cfg = new ConfigFile();
        bool hasSaved = cfg.Load(ConfigPath) == Error.Ok;

        foreach (var action in Actions)
        {
            if (!InputMap.HasAction(action.Name))
                InputMap.AddAction(action.Name);
            InputMap.ActionEraseEvents(action.Name);

            InputEvent evt = action.Default;
            if (hasSaved && cfg.HasSectionKey(Section, action.Name))
            {
                var decoded = Decode((string)cfg.GetValue(Section, action.Name));
                if (decoded is not null) evt = decoded;
            }
            InputMap.ActionAddEvent(action.Name, evt);
        }
    }

    public static InputEvent? CurrentBinding(string actionName)
    {
        var events = InputMap.ActionGetEvents(actionName);
        return events.Count > 0 ? events[0] : null;
    }

    /// <summary>Rebinds an action to a single event (replacing any prior binding) and saves immediately.</summary>
    public static void Rebind(string actionName, InputEvent newEvent)
    {
        InputMap.ActionEraseEvents(actionName);
        InputMap.ActionAddEvent(actionName, newEvent);
        Save();
    }

    /// <summary>Reverts every action to its Default binding and saves immediately.</summary>
    public static void ResetToDefaults()
    {
        foreach (var action in Actions)
        {
            InputMap.ActionEraseEvents(action.Name);
            InputMap.ActionAddEvent(action.Name, action.Default);
        }
        Save();
    }

    private static void Save()
    {
        var cfg = new ConfigFile();
        foreach (var action in Actions)
        {
            var evt = CurrentBinding(action.Name);
            if (evt is not null) cfg.SetValue(Section, action.Name, Encode(evt));
        }
        cfg.Save(ConfigPath);
    }

    public static string DisplayString(InputEvent? evt) => evt switch
    {
        InputEventKey k => OS.GetKeycodeString(k.PhysicalKeycode),
        InputEventMouseButton { ButtonIndex: MouseButton.Left } => "Mouse Left",
        InputEventMouseButton { ButtonIndex: MouseButton.Right } => "Mouse Right",
        InputEventMouseButton { ButtonIndex: MouseButton.Middle } => "Mouse Middle",
        InputEventMouseButton m => $"Mouse {m.ButtonIndex}",
        _ => "Unbound",
    };

    // Stored as "key:<code>" / "mouse:<index>" rather than serialized Resources,
    // since InputEvent Resources have no source path for ConfigFile to save by reference.
    private static string Encode(InputEvent evt) => evt switch
    {
        InputEventKey k => $"key:{(int)k.PhysicalKeycode}",
        InputEventMouseButton m => $"mouse:{(int)m.ButtonIndex}",
        _ => "",
    };

    private static InputEvent? Decode(string encoded)
    {
        var parts = encoded.Split(':');
        if (parts.Length != 2) return null;

        if (parts[0] == "key" && int.TryParse(parts[1], out int keyCode))
            return new InputEventKey { PhysicalKeycode = (Key)keyCode };
        if (parts[0] == "mouse" && int.TryParse(parts[1], out int buttonIndex))
            return new InputEventMouseButton { ButtonIndex = (MouseButton)buttonIndex };
        return null;
    }
}
