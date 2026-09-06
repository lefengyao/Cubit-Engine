using System.Numerics;
using Cubit.Physics.Scene;

namespace Cubit.Physics.Simulation;

/// <summary>固定迭代的线性接触求解器。</summary>
internal sealed class PhysicsSolver
{
    public const int IterationCount = 4;
    private const float PositionalSlop = 0.002f;
    private const float PositionalCorrection = 0.2f;

    public void Solve(PhysicsWorld3D world, List<PhysicsContact> contacts, float delta)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(contacts);
        if (!float.IsFinite(delta) || delta <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        for (var iteration = 0; iteration < IterationCount; iteration++)
        {
            foreach (var contact in contacts)
            {
                SolveVelocity(contact);
            }
        }

        foreach (var contact in contacts)
        {
            SolvePosition(world, contact);
        }
    }

    private static void SolveVelocity(in PhysicsContact contact)
    {
        var first = contact.First as RigidBody3D;
        var second = contact.Second as RigidBody3D;
        var firstInverseMass = first?.InverseMass ?? 0f;
        var secondInverseMass = second?.InverseMass ?? 0f;
        var inverseMass = firstInverseMass + secondInverseMass;
        if (inverseMass <= 0f)
        {
            return;
        }

        var relativeVelocity = (first?.LinearVelocity ?? Vector3.Zero) - (second?.LinearVelocity ?? Vector3.Zero);
        var normalVelocity = Vector3.Dot(relativeVelocity, contact.Normal);
        var normalImpulse = 0f;
        if (normalVelocity < 0f)
        {
            var restitution = MathF.Max(
                contact.First.PhysicsMaterial?.Restitution ?? 0f,
                contact.Second.PhysicsMaterial?.Restitution ?? 0f);
            normalImpulse = -(1f + restitution) * normalVelocity / inverseMass;
            var impulse = contact.Normal * normalImpulse;
            if (first is not null)
            {
                first.LinearVelocity += impulse * firstInverseMass;
            }

            if (second is not null)
            {
                second.LinearVelocity -= impulse * secondInverseMass;
            }
        }

        relativeVelocity = (first?.LinearVelocity ?? Vector3.Zero) - (second?.LinearVelocity ?? Vector3.Zero);
        var tangent = relativeVelocity - contact.Normal * Vector3.Dot(relativeVelocity, contact.Normal);
        var tangentLengthSquared = tangent.LengthSquared();
        if (tangentLengthSquared <= 1e-12f || normalImpulse <= 0f)
        {
            return;
        }

        tangent /= MathF.Sqrt(tangentLengthSquared);
        var tangentImpulse = -Vector3.Dot(relativeVelocity, tangent) / inverseMass;
        var friction = MathF.Sqrt(
            (contact.First.PhysicsMaterial?.Friction ?? 0.5f) *
            (contact.Second.PhysicsMaterial?.Friction ?? 0.5f));
        tangentImpulse = Math.Clamp(tangentImpulse, -normalImpulse * friction, normalImpulse * friction);
        var frictionImpulse = tangent * tangentImpulse;
        if (first is not null)
        {
            first.LinearVelocity += frictionImpulse * firstInverseMass;
        }

        if (second is not null)
        {
            second.LinearVelocity -= frictionImpulse * secondInverseMass;
        }
    }

    private static void SolvePosition(PhysicsWorld3D world, in PhysicsContact contact)
    {
        var first = contact.First as RigidBody3D;
        var second = contact.Second as RigidBody3D;
        var firstInverseMass = first?.InverseMass ?? 0f;
        var secondInverseMass = second?.InverseMass ?? 0f;
        var inverseMass = firstInverseMass + secondInverseMass;
        var correctionDistance = MathF.Max(contact.Penetration - PositionalSlop, 0f);
        if (inverseMass <= 0f || correctionDistance <= 0f)
        {
            return;
        }

        var correction = contact.Normal * (correctionDistance * PositionalCorrection / inverseMass);
        if (first is not null)
        {
            world.ApplySolverDisplacement(first, correction * firstInverseMass);
        }

        if (second is not null)
        {
            world.ApplySolverDisplacement(second, -correction * secondInverseMass);
        }
    }
}
