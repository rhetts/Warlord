using Godot;

namespace Warlord.Game;

/// <summary>
/// Tank-style movement for the character-mode preview: up/down arrows walk
/// forward/back along the character's current facing, left/right arrows
/// turn in place. This node (CharacterView) only ever translates — it never
/// rotates itself, so the camera (a direct child of it, see
/// IsoMap.BuildCharacterView) keeps a fixed facing and just follows the walk.
/// Turning instead rotates ModelPivot, a child node holding the character
/// model, so only the model spins in place in front of the fixed camera.
/// Switches between the model's own Idle/Walking animations based on
/// whether it's currently translating, rather than a fixed spin (see prior
/// version) or hand-posed bones (see the abandoned elbow-bend attempt).
/// </summary>
public partial class CharacterView : Node3D
{
    private const float WalkSpeed = 1.4f;        // world units/sec
    private const float TurnSpeedDegrees = 100f; // degrees/sec

    public Node3D ModelPivot { get; } = new();

    private AnimationPlayer? _animPlayer;
    private string _currentAnim = "";

    public override void _Ready()
    {
        AddChild(ModelPivot);
    }

    public void SetAnimationPlayer(AnimationPlayer player)
    {
        _animPlayer = player;
        PlayAnimation("HumanArmature|Idle");
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        float turn = Input.GetAxis("ui_right", "ui_left");
        float move = Input.GetAxis("ui_down", "ui_up");

        if (turn != 0f)
            ModelPivot.RotateY(Mathf.DegToRad(TurnSpeedDegrees) * turn * dt);

        if (move != 0f)
        {
            Vector3 forward = -ModelPivot.Transform.Basis.Z;
            Position += forward * move * WalkSpeed * dt;
        }

        PlayAnimation(move != 0f ? "HumanArmature|Walking" : "HumanArmature|Idle");
    }

    private void PlayAnimation(string name)
    {
        if (_animPlayer is null || _currentAnim == name || !_animPlayer.HasAnimation(name)) return;
        _animPlayer.GetAnimation(name).LoopMode = Animation.LoopModeEnum.Linear;
        _animPlayer.Play(name);
        _currentAnim = name;
    }
}
