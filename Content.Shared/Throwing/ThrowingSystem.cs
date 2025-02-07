using Content.Shared.Gravity;
using Content.Shared.Interaction;
using Content.Shared.Movement.Components;
using Content.Shared.Projectiles;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared.Throwing;

public sealed class ThrowingSystem : EntitySystem
{
    public const float ThrowAngularImpulse = 5f;

    public const float PushbackDefault = 1f;

    /// <summary>
    /// The minimum amount of time an entity needs to be thrown before the timer can be run.
    /// Anything below this threshold never enters the air.
    /// </summary>
    public const float FlyTime = 0.15f;

    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly SharedInteractionSystem _interactionSystem = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ThrownItemSystem _thrownSystem = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;

    public void TryThrow(
        EntityUid uid,
        EntityCoordinates coordinates,
        float strength = 1.0f,
        EntityUid? user = null,
        float pushbackRatio = PushbackDefault,
        bool playSound = true)
    {
        var thrownPos = Transform(uid).MapPosition;
        var mapPos = coordinates.ToMap(EntityManager, _transform);

        if (mapPos.MapId != thrownPos.MapId)
            return;

        TryThrow(uid, mapPos.Position - thrownPos.Position, strength, user, pushbackRatio, playSound);
    }

    /// <summary>
    ///     Tries to throw the entity if it has a physics component, otherwise does nothing.
    /// </summary>
    /// <param name="uid">The entity being thrown.</param>
    /// <param name="direction">A vector pointing from the entity to its destination.</param>
    /// <param name="strength">How much the direction vector should be multiplied for velocity.</param>
    /// <param name="pushbackRatio">The ratio of impulse applied to the thrower - defaults to 10 because otherwise it's not enough to properly recover from getting spaced</param>
    public void TryThrow(EntityUid uid,
        Vector2 direction,
        float strength = 1.0f,
        EntityUid? user = null,
        float pushbackRatio = PushbackDefault,
        bool playSound = true)
    {
        var physicsQuery = GetEntityQuery<PhysicsComponent>();
        if (!physicsQuery.TryGetComponent(uid, out var physics))
            return;

        var projectileQuery = GetEntityQuery<ProjectileComponent>();
        var tagQuery = GetEntityQuery<TagComponent>();

        TryThrow(
            uid,
            direction,
            physics,
            Transform(uid),
            projectileQuery,
            tagQuery,
            strength,
            user,
            pushbackRatio,
            playSound);
    }

    /// <summary>
    ///     Tries to throw the entity if it has a physics component, otherwise does nothing.
    /// </summary>
    /// <param name="uid">The entity being thrown.</param>
    /// <param name="direction">A vector pointing from the entity to its destination.</param>
    /// <param name="strength">How much the direction vector should be multiplied for velocity.</param>
    /// <param name="pushbackRatio">The ratio of impulse applied to the thrower - defaults to 10 because otherwise it's not enough to properly recover from getting spaced</param>
    public void TryThrow(EntityUid uid,
        Vector2 direction,
        PhysicsComponent physics,
        TransformComponent transform,
        EntityQuery<ProjectileComponent> projectileQuery,
        EntityQuery<TagComponent> tagQuery,
        float strength = 1.0f,
        EntityUid? user = null,
        float pushbackRatio = PushbackDefault,
        bool playSound = true)
    {
        if (strength <= 0 || direction == Vector2.Infinity || direction == Vector2.NaN || direction == Vector2.Zero)
            return;

        if ((physics.BodyType & (BodyType.Dynamic | BodyType.KinematicController)) == 0x0)
        {
            Logger.Warning($"Tried to throw entity {ToPrettyString(uid)} but can't throw {physics.BodyType} bodies!");
            return;
        }

        if (projectileQuery.HasComponent(uid))
            return;

        var comp = EnsureComp<ThrownItemComponent>(uid);
        comp.Thrower = user;

        // Give it a l'il spin.
        if (physics.InvI > 0f && (!tagQuery.TryGetComponent(uid, out var tag) || !_tagSystem.HasTag(tag, "NoSpinOnThrow")))
            _physics.ApplyAngularImpulse(uid, ThrowAngularImpulse / physics.InvI, body: physics);
        else
            transform.LocalRotation = direction.ToWorldAngle() - Math.PI;

        if (user != null)
            _interactionSystem.ThrownInteraction(user.Value, uid);

        var impulseVector = direction.Normalized * strength * physics.Mass;
        _physics.ApplyLinearImpulse(uid, impulseVector, body: physics);

        // Estimate time to arrival so we can apply OnGround status and slow it much faster.
        var time = direction.Length / strength;

        if (time < FlyTime)
        {
            _thrownSystem.LandComponent(uid, comp, physics, playSound);
        }
        else
        {
            _physics.SetBodyStatus(physics, BodyStatus.InAir);

            Timer.Spawn(TimeSpan.FromSeconds(time - FlyTime), () =>
            {
                if (physics.Deleted)
                    return;

                _thrownSystem.LandComponent(uid, comp, physics, playSound);
            });
        }

        // Give thrower an impulse in the other direction
        if (user != null &&
            pushbackRatio != 0.0f &&
            physics.Mass > 0f &&
            TryComp(user.Value, out PhysicsComponent? userPhysics) &&
            _gravity.IsWeightless(user.Value, userPhysics))
        {
            var msg = new ThrowPushbackAttemptEvent();
            RaiseLocalEvent(uid, msg);

            if (!msg.Cancelled)
                _physics.ApplyLinearImpulse(user.Value, -impulseVector * pushbackRatio * physics.Mass, body: userPhysics);
        }
    }
}
