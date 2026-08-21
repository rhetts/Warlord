using System.Collections.Generic;
using Godot;

namespace Warlord.Game;

/// <summary>
/// Third view: a full-screen overlay (same pattern as the character viewport
/// in IsoMap) listing every KeyBindings action with its current binding and a
/// button to capture a new one. Click a binding button, then press any key or
/// mouse button; Escape cancels. Rebinds persist immediately via KeyBindings.
/// </summary>
public partial class SettingsView : Control
{
    private string? _waitingForAction;
    private readonly Dictionary<string, Button> _bindingButtons = new();

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var background = new ColorRect { Color = new Color(0.08f, 0.08f, 0.10f, 0.92f) };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var list = new VBoxContainer { Position = new Vector2(60, 60) };
        AddChild(list);

        list.AddChild(new Label { Text = "Key Bindings" });

        foreach (var action in KeyBindings.Actions)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(360, 32) };

            row.AddChild(new Label { Text = action.Label, CustomMinimumSize = new Vector2(180, 0) });

            var bindingButton = new Button
            {
                Text = KeyBindings.DisplayString(KeyBindings.CurrentBinding(action.Name)),
                CustomMinimumSize = new Vector2(140, 28),
            };
            bindingButton.Pressed += () => StartRebind(action.Name, bindingButton);
            _bindingButtons[action.Name] = bindingButton;

            row.AddChild(bindingButton);
            list.AddChild(row);
        }

        var resetButton = new Button { Text = "Set Defaults", CustomMinimumSize = new Vector2(140, 32) };
        resetButton.Pressed += ResetToDefaults;
        list.AddChild(resetButton);
    }

    private void ResetToDefaults()
    {
        CancelRebind();
        KeyBindings.ResetToDefaults();
        foreach (var action in KeyBindings.Actions)
            _bindingButtons[action.Name].Text = KeyBindings.DisplayString(KeyBindings.CurrentBinding(action.Name));
    }

    private void StartRebind(string actionName, Button button)
    {
        _waitingForAction = actionName;
        button.Text = "Press a key...";
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible || _waitingForAction is null) return;

        if (@event is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape })
        {
            CancelRebind();
            GetViewport().SetInputAsHandled();
            return;
        }

        InputEvent? captured = @event switch
        {
            InputEventKey { Pressed: true, Echo: false } k => k,
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } m => m,
            _ => null,
        };
        if (captured is null) return;

        string actionName = _waitingForAction;
        KeyBindings.Rebind(actionName, captured);
        _bindingButtons[actionName].Text = KeyBindings.DisplayString(captured);
        _waitingForAction = null;
        GetViewport().SetInputAsHandled();
    }

    private void CancelRebind()
    {
        if (_waitingForAction is null) return;
        _bindingButtons[_waitingForAction].Text = KeyBindings.DisplayString(KeyBindings.CurrentBinding(_waitingForAction));
        _waitingForAction = null;
    }
}
