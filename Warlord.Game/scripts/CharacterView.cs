using Godot;

namespace Warlord.Game;

/// <summary>
/// Third-person controller for the character-mode preview. Arrow keys
/// (cam_orbit_*) orbit the camera around the character via CameraRig
/// (yaw) and CameraPitchPivot (pitch, a rigid child of CameraRig) — see
/// IsoMap.BuildCharacterView for how the camera is attached under
/// CameraPitchPivot so it orbits and keeps looking at the character without
/// an explicit per-frame LookAt. CameraRig is independent of ModelPivot, so
/// orbiting the camera never turns the model.
///
/// WASD is tank-style and independent of the camera: A/D (move_left/
/// move_right) turn the model's facing in place, W/S (move_forward/
/// move_back) walk forward/back along whatever direction it's currently
/// facing. Mouse left/right (swing_*_arm) play a one-shot sword-swing
/// animation; space (jump) plays a scripted jump arc.
///
/// This node (CharacterView) only ever translates.
/// </summary>
public partial class CharacterView : Node3D
{
    private const float WalkSpeed = 1.4f;          // world units/sec, top speed
    private const float WalkAcceleration = 5f;     // world units/sec^2, ramp to/from WalkSpeed
    private const float TurnSpeedDegrees = 100f;   // model facing (A/D), degrees/sec, top speed
    private const float TurnAcceleration = 500f;   // degrees/sec^2, ramp to/from TurnSpeedDegrees
    private const float OrbitSpeedDegrees = 100f;  // camera yaw/pitch (arrows), degrees/sec
    private const float MinPitchDegrees = -60f;
    private const float MaxPitchDegrees = 40f;
    private const float JumpHeight = 1.0f;   // world units
    private const float JumpDuration = 0.6f; // seconds
    private const float FallbackClipSeconds = 0.7f; // used only if an animation's own Length reads as 0
    private const float AnimationBlendSeconds = 0.15f; // crossfade duration between any two animations

    public Node3D ModelPivot { get; } = new();
    public Node3D CameraRig { get; } = new();
    public Node3D CameraPitchPivot { get; } = new() { Position = new Vector3(0f, 1.3f, 0f) };

    private AnimationPlayer? _animPlayer;
    private string _currentAnim = "";
    private float _pitchDegrees;
    private float _currentWalkSpeed;
    private float _currentTurnSpeed;
    private bool _jumping;
    private float _jumpElapsed;
    private bool _swinging;
    private double _swingEndsAtSec;

    public override void _Ready()
    {
        AddChild(ModelPivot);
        AddChild(CameraRig);
        CameraRig.AddChild(CameraPitchPivot);
    }

    public void SetAnimationPlayer(AnimationPlayer player)
    {
        _animPlayer = player;
        PlayAnimation("HumanArmature|Idle", loop: true);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        HandleCameraOrbit(dt);
        bool moving = HandleMovement(dt);
        HandleJump(dt);
        HandleSwing();

        if (!_jumping && !_swinging)
            PlayAnimation(moving ? "HumanArmature|Walking" : "HumanArmature|Idle", loop: true);
    }

    private void HandleCameraOrbit(float dt)
    {
        float yaw = Input.GetAxis("cam_orbit_right", "cam_orbit_left");
        if (yaw != 0f)
            CameraRig.RotateY(Mathf.DegToRad(OrbitSpeedDegrees) * yaw * dt);

        float pitch = Input.GetAxis("cam_orbit_down", "cam_orbit_up");
        if (pitch != 0f)
        {
            _pitchDegrees = Mathf.Clamp(_pitchDegrees + pitch * OrbitSpeedDegrees * dt, MinPitchDegrees, MaxPitchDegrees);
            CameraPitchPivot.Rotation = new Vector3(Mathf.DegToRad(_pitchDegrees), 0f, 0f);
        }
    }

    // Both axes ease toward their target speed (input * top speed) rather
    // than snapping to it, so starting, stopping, and reversing all read as
    // acceleration/deceleration instead of an instant on/off.
    private bool HandleMovement(float dt)
    {
        float turnTarget = Input.GetAxis("move_left", "move_right") * TurnSpeedDegrees;
        _currentTurnSpeed = Mathf.MoveToward(_currentTurnSpeed, turnTarget, TurnAcceleration * dt);
        if (_currentTurnSpeed != 0f)
            ModelPivot.RotateY(-Mathf.DegToRad(_currentTurnSpeed) * dt);

        float moveTarget = Input.GetAxis("move_back", "move_forward") * WalkSpeed;
        _currentWalkSpeed = Mathf.MoveToward(_currentWalkSpeed, moveTarget, WalkAcceleration * dt);
        if (Mathf.IsZeroApprox(_currentWalkSpeed)) return false;

        Vector3 forward = -ModelPivot.Transform.Basis.Z;
        Position += forward * _currentWalkSpeed * dt;
        return true;
    }

    private void HandleJump(float dt)
    {
        if (!_jumping)
        {
            if (Input.IsActionJustPressed("jump"))
            {
                _jumping = true;
                _jumpElapsed = 0f;
                PlayAnimation("HumanArmature|Jump", loop: false);
            }
            return;
        }

        _jumpElapsed += dt;
        float t = _jumpElapsed / JumpDuration;
        var pos = Position;
        if (t >= 1f)
        {
            _jumping = false;
            pos.Y = 0f;
        }
        else
        {
            pos.Y = JumpHeight * Mathf.Sin(Mathf.Pi * t);
        }
        Position = pos;
    }

    private void HandleSwing()
    {
        if (_swinging)
        {
            if (_animPlayer is null || NowSeconds() >= _swingEndsAtSec)
                _swinging = false;
            return;
        }

        if (Input.IsActionJustPressed("swing_left_arm"))
            StartSwing("HumanArmature|Run_swordAttack");
        else if (Input.IsActionJustPressed("swing_right_arm"))
            StartSwing("HumanArmature|Run_swordAttackK");
    }

    private void StartSwing(string animName)
    {
        if (_animPlayer is null || !_animPlayer.HasAnimation(animName)) return;
        double length = _animPlayer.GetAnimation(animName).Length;
        _swinging = true;
        _swingEndsAtSec = NowSeconds() + (length > 0 ? length : FallbackClipSeconds);
        PlayAnimation(animName, loop: false);
    }

    private static double NowSeconds() => Time.GetTicksMsec() / 1000.0;

    private void PlayAnimation(string name, bool loop)
    {
        if (_animPlayer is null || _currentAnim == name || !_animPlayer.HasAnimation(name)) return;
        _animPlayer.GetAnimation(name).LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        _animPlayer.Play(name, AnimationBlendSeconds);
        _currentAnim = name;
    }
}
