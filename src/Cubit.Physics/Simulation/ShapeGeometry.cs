using System.Numerics;
using Cubit.Core.Scene;
using Cubit.Core.Spatial;
using Cubit.Physics.Shapes;

namespace Cubit.Physics.Simulation;

/// <summary>基础凸形状的精确射线和重叠几何，不把 AABB 当作最终命中。</summary>
internal static class ShapeGeometry
{
    private const float Epsilon = 1e-6f;
    private const float EpaTolerance = 1e-4f;
    private const int GjkIterationLimit = 32;
    private const int EpaIterationLimit = 32;
    private const int EpaVertexLimit = 64;
    private const int EpaFaceLimit = 128;
    internal const int MaximumSweepSampleCount = 256;

    public static bool Raycast(
        Shape3D shape,
        in Transform3D transform,
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        out ShapeRayHit hit)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var inverseRotation = Quaternion.Conjugate(transform.Rotation);
        var localOrigin = Vector3.Transform(origin - transform.Position, inverseRotation) / transform.Scale;
        var localDirection = Vector3.Transform(direction, inverseRotation);
        var localMaxDistance = maxDistance / transform.Scale;
        var found = shape switch
        {
            BoxShape3D box => RaycastBox(box.GetLocalBounds(), localOrigin, localDirection, localMaxDistance, out hit),
            SphereShape3D sphere => RaycastSphere(Vector3.Zero, sphere.Radius, localOrigin, localDirection, localMaxDistance, out hit),
            CapsuleShape3D capsule => RaycastCapsule(capsule, localOrigin, localDirection, localMaxDistance, out hit),
            _ => throw new NotSupportedException($"不支持的 Shape3D 类型: {shape.GetType().FullName}"),
        };
        if (!found)
        {
            return false;
        }

        var worldDistance = hit.Distance * transform.Scale;
        if (!float.IsFinite(worldDistance) || worldDistance > maxDistance)
        {
            hit = default;
            return false;
        }

        hit = hit with
        {
            Distance = worldDistance,
            Position = origin + direction * worldDistance,
            Normal = Vector3.Normalize(Vector3.Transform(hit.Normal, transform.Rotation)),
        };
        return true;
    }

    public static bool Overlaps(
        Shape3D left,
        in Transform3D leftTransform,
        Shape3D right,
        in Transform3D rightTransform)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        Span<Vector3> simplex = stackalloc Vector3[4];
        var count = 0;
        var direction = rightTransform.Position - leftTransform.Position;
        if (direction.LengthSquared() <= Epsilon * Epsilon)
        {
            direction = Vector3.UnitX;
        }

        for (var iteration = 0; iteration < GjkIterationLimit; iteration++)
        {
            var directionLengthSquared = direction.LengthSquared();
            if (!float.IsFinite(directionLengthSquared))
            {
                return false;
            }

            if (directionLengthSquared <= Epsilon * Epsilon)
            {
                return true;
            }

            var point = Support(left, leftTransform, direction) - Support(right, rightTransform, -direction);
            if (!IsFinite(point))
            {
                return false;
            }

            if (Vector3.Dot(point, direction) < 0f)
            {
                return false;
            }

            for (var simplexIndex = 0; simplexIndex < count; simplexIndex++)
            {
                if (Vector3.DistanceSquared(point, simplex[simplexIndex]) <= Epsilon * Epsilon)
                {
                    return false;
                }
            }

            for (var index = count; index > 0; index--)
            {
                simplex[index] = simplex[index - 1];
            }

            simplex[0] = point;
            count++;
            if (UpdateSimplex(simplex, ref count, ref direction))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 对两个基础凸形状执行有界的连续扫掠。每个采样点仍使用精确窄相位，
    /// 包围盒只由调用方用于候选筛选。
    /// </summary>
    /// <summary>为已重叠的凸形状构造有界 GJK+EPA 接触。</summary>
    public static bool TryGetContact(
        Shape3D first,
        in Transform3D firstTransform,
        Shape3D second,
        in Transform3D secondTransform,
        out ShapeContact contact)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (!Overlaps(first, firstTransform, second, secondTransform))
        {
            contact = default;
            return false;
        }

        if (TryGetAnalyticContact(first, firstTransform, second, secondTransform, out contact))
        {
            return true;
        }

        if (TryGetEpaContact(first, firstTransform, second, secondTransform, out contact))
        {
            return true;
        }

        // 退化单纯形（共面接触等）不能组成 EPA 初始四面体时保留稳定投影退路。
        var fallback = firstTransform.Position - secondTransform.Position;
        if (fallback.LengthSquared() <= Epsilon * Epsilon)
        {
            fallback = Vector3.UnitY;
        }
        else
        {
            fallback = Vector3.Normalize(fallback);
        }

        var normal = GetContactNormal(second, secondTransform, firstTransform.Position, fallback);
        var firstPoint = Support(first, firstTransform, -normal);
        var secondPoint = Support(second, secondTransform, normal);
        var penetration = MathF.Max(0f, -Vector3.Dot(firstPoint - secondPoint, normal));
        contact = new ShapeContact(
            (firstPoint + secondPoint) * 0.5f,
            normal,
            penetration,
            GetContactFeatureId(first, firstTransform, second, secondTransform, normal));
        return true;
    }

    private static bool TryGetAnalyticContact(
        Shape3D first,
        in Transform3D firstTransform,
        Shape3D second,
        in Transform3D secondTransform,
        out ShapeContact contact)
    {
        switch (first, second)
        {
            case (SphereShape3D firstSphere, SphereShape3D secondSphere):
                return TryGetSphereSphereContact(firstSphere, firstTransform, secondSphere, secondTransform, out contact);
            case (SphereShape3D sphere, BoxShape3D box):
                return TryGetSphereBoxContact(sphere, firstTransform, box, secondTransform, out contact);
            case (BoxShape3D box, SphereShape3D sphere):
                if (TryGetSphereBoxContact(sphere, secondTransform, box, firstTransform, out var sphereContact))
                {
                    contact = CreateContact(
                        first,
                        firstTransform,
                        second,
                        secondTransform,
                        -sphereContact.Normal,
                        sphereContact.Penetration);
                    return true;
                }

                break;
            case (SphereShape3D sphere, CapsuleShape3D capsule):
                return TryGetSphereCapsuleContact(sphere, firstTransform, capsule, secondTransform, out contact);
            case (CapsuleShape3D capsule, SphereShape3D sphere):
                if (TryGetSphereCapsuleContact(sphere, secondTransform, capsule, firstTransform, out var capsuleSphereContact))
                {
                    contact = CreateContact(
                        first,
                        firstTransform,
                        second,
                        secondTransform,
                        -capsuleSphereContact.Normal,
                        capsuleSphereContact.Penetration);
                    return true;
                }

                break;
            case (CapsuleShape3D capsule, BoxShape3D box):
                return TryGetAxisAlignedCapsuleBoxContact(capsule, firstTransform, box, secondTransform, out contact);
            case (CapsuleShape3D firstCapsule, CapsuleShape3D secondCapsule):
                return TryGetCapsuleCapsuleContact(
                    firstCapsule,
                    firstTransform,
                    secondCapsule,
                    secondTransform,
                    out contact);
        }

        contact = default;
        return false;
    }

    private static bool TryGetSphereSphereContact(
        SphereShape3D first,
        in Transform3D firstTransform,
        SphereShape3D second,
        in Transform3D secondTransform,
        out ShapeContact contact)
    {
        var difference = firstTransform.Position - secondTransform.Position;
        var distanceSquared = difference.LengthSquared();
        if (!float.IsFinite(distanceSquared))
        {
            contact = default;
            return false;
        }

        var distance = MathF.Sqrt(distanceSquared);
        var penetration = first.Radius * firstTransform.Scale + second.Radius * secondTransform.Scale - distance;
        if (penetration < 0f)
        {
            contact = default;
            return false;
        }

        var normal = distance > Epsilon ? difference / distance : Vector3.UnitY;
        contact = CreateContact(first, firstTransform, second, secondTransform, normal, penetration);
        return true;
    }

    private static bool TryGetSphereBoxContact(
        SphereShape3D sphere,
        in Transform3D sphereTransform,
        BoxShape3D box,
        in Transform3D boxTransform,
        out ShapeContact contact)
    {
        var inverseRotation = Quaternion.Conjugate(boxTransform.Rotation);
        var localCenter = Vector3.Transform(sphereTransform.Position - boxTransform.Position, inverseRotation) /
            boxTransform.Scale;
        var bounds = box.GetLocalBounds();
        var closest = Vector3.Clamp(localCenter, bounds.Min, bounds.Max);
        var localDifference = localCenter - closest;
        var localDistanceSquared = localDifference.LengthSquared();
        if (!float.IsFinite(localDistanceSquared))
        {
            contact = default;
            return false;
        }

        var sphereRadius = sphere.Radius * sphereTransform.Scale;
        Vector3 localNormal;
        float penetration;
        if (localDistanceSquared > Epsilon * Epsilon)
        {
            var localDistance = MathF.Sqrt(localDistanceSquared);
            localNormal = localDifference / localDistance;
            penetration = sphereRadius - localDistance * boxTransform.Scale;
        }
        else
        {
            localNormal = GetBoxContactNormal(bounds, localCenter);
            var distanceToFace = GetDistanceToBoxFace(bounds, localCenter, localNormal);
            penetration = sphereRadius + distanceToFace * boxTransform.Scale;
        }

        if (penetration < 0f)
        {
            contact = default;
            return false;
        }

        var normal = Vector3.Normalize(Vector3.Transform(localNormal, boxTransform.Rotation));
        contact = CreateContact(sphere, sphereTransform, box, boxTransform, normal, penetration);
        return true;
    }

    private static bool TryGetSphereCapsuleContact(
        SphereShape3D sphere,
        in Transform3D sphereTransform,
        CapsuleShape3D capsule,
        in Transform3D capsuleTransform,
        out ShapeContact contact)
    {
        GetCapsuleSegment(capsule, capsuleTransform, out var start, out var end, out var capsuleRadius);
        var nearest = ClosestPointOnSegment(sphereTransform.Position, start, end);
        var difference = sphereTransform.Position - nearest;
        var distanceSquared = difference.LengthSquared();
        if (!float.IsFinite(distanceSquared))
        {
            contact = default;
            return false;
        }

        var distance = MathF.Sqrt(distanceSquared);
        var penetration = sphere.Radius * sphereTransform.Scale + capsuleRadius - distance;
        if (penetration < 0f)
        {
            contact = default;
            return false;
        }

        var axis = end - start;
        var normal = distance > Epsilon
            ? difference / distance
            : NormalizeSupportDirection(Vector3.Cross(axis, Vector3.UnitX));
        contact = CreateContact(sphere, sphereTransform, capsule, capsuleTransform, normal, penetration);
        return true;
    }

    private static bool TryGetCapsuleCapsuleContact(
        CapsuleShape3D first,
        in Transform3D firstTransform,
        CapsuleShape3D second,
        in Transform3D secondTransform,
        out ShapeContact contact)
    {
        GetCapsuleSegment(first, firstTransform, out var firstStart, out var firstEnd, out var firstRadius);
        GetCapsuleSegment(second, secondTransform, out var secondStart, out var secondEnd, out var secondRadius);
        ClosestPointsOnSegments(firstStart, firstEnd, secondStart, secondEnd, out var firstPoint, out var secondPoint);
        var difference = firstPoint - secondPoint;
        var distanceSquared = difference.LengthSquared();
        if (!float.IsFinite(distanceSquared))
        {
            contact = default;
            return false;
        }

        var distance = MathF.Sqrt(distanceSquared);
        var penetration = firstRadius + secondRadius - distance;
        if (penetration < 0f)
        {
            contact = default;
            return false;
        }

        var normal = distance > Epsilon
            ? difference / distance
            : NormalizeSupportDirection(Vector3.Cross(firstEnd - firstStart, secondEnd - secondStart));
        contact = CreateContact(first, firstTransform, second, secondTransform, normal, penetration);
        return true;
    }

    private static ShapeContact CreateContact(
        Shape3D first,
        in Transform3D firstTransform,
        Shape3D second,
        in Transform3D secondTransform,
        in Vector3 normal,
        float penetration)
    {
        var firstPoint = Support(first, firstTransform, -normal);
        var secondPoint = Support(second, secondTransform, normal);
        return new ShapeContact(
            (firstPoint + secondPoint) * 0.5f,
            normal,
            MathF.Max(0f, penetration),
            GetContactFeatureId(first, firstTransform, second, secondTransform, normal));
    }

    private static float GetDistanceToBoxFace(in Aabb3 bounds, in Vector3 point, in Vector3 normal) =>
        normal.X < 0f ? point.X - bounds.Min.X :
        normal.X > 0f ? bounds.Max.X - point.X :
        normal.Y < 0f ? point.Y - bounds.Min.Y :
        normal.Y > 0f ? bounds.Max.Y - point.Y :
        normal.Z < 0f ? point.Z - bounds.Min.Z : bounds.Max.Z - point.Z;

    private static void GetCapsuleSegment(
        CapsuleShape3D capsule,
        in Transform3D transform,
        out Vector3 start,
        out Vector3 end,
        out float radius)
    {
        var axis = Vector3.Transform(Vector3.UnitY, transform.Rotation);
        var offset = axis * (capsule.CylinderHeight * transform.Scale * 0.5f);
        start = transform.Position - offset;
        end = transform.Position + offset;
        radius = capsule.Radius * transform.Scale;
    }

    private static Vector3 ClosestPointOnSegment(in Vector3 point, in Vector3 start, in Vector3 end)
    {
        var direction = end - start;
        var lengthSquared = direction.LengthSquared();
        if (lengthSquared <= Epsilon * Epsilon)
        {
            return start;
        }

        var fraction = Math.Clamp(Vector3.Dot(point - start, direction) / lengthSquared, 0f, 1f);
        return start + direction * fraction;
    }

    private static void ClosestPointsOnSegments(
        in Vector3 firstStart,
        in Vector3 firstEnd,
        in Vector3 secondStart,
        in Vector3 secondEnd,
        out Vector3 firstPoint,
        out Vector3 secondPoint)
    {
        var firstDirection = firstEnd - firstStart;
        var secondDirection = secondEnd - secondStart;
        var offset = firstStart - secondStart;
        var firstLengthSquared = Vector3.Dot(firstDirection, firstDirection);
        var secondLengthSquared = Vector3.Dot(secondDirection, secondDirection);
        var cross = Vector3.Dot(firstDirection, secondDirection);
        var firstOffset = Vector3.Dot(firstDirection, offset);
        var secondOffset = Vector3.Dot(secondDirection, offset);
        float firstFraction;
        float secondFraction;
        if (firstLengthSquared <= Epsilon && secondLengthSquared <= Epsilon)
        {
            firstFraction = 0f;
            secondFraction = 0f;
        }
        else if (firstLengthSquared <= Epsilon)
        {
            firstFraction = 0f;
            secondFraction = Math.Clamp(secondOffset / secondLengthSquared, 0f, 1f);
        }
        else
        {
            if (secondLengthSquared <= Epsilon)
            {
                secondFraction = 0f;
                firstFraction = Math.Clamp(-firstOffset / firstLengthSquared, 0f, 1f);
            }
            else
            {
                var denominator = firstLengthSquared * secondLengthSquared - cross * cross;
                firstFraction = denominator > Epsilon
                    ? Math.Clamp((cross * secondOffset - firstOffset * secondLengthSquared) / denominator, 0f, 1f)
                    : 0f;
                secondFraction = (cross * firstFraction + secondOffset) / secondLengthSquared;
                if (secondFraction < 0f)
                {
                    secondFraction = 0f;
                    firstFraction = Math.Clamp(-firstOffset / firstLengthSquared, 0f, 1f);
                }
                else if (secondFraction > 1f)
                {
                    secondFraction = 1f;
                    firstFraction = Math.Clamp((cross - firstOffset) / firstLengthSquared, 0f, 1f);
                }
            }
        }

        firstPoint = firstStart + firstDirection * firstFraction;
        secondPoint = secondStart + secondDirection * secondFraction;
    }

    private static bool TryGetEpaContact(
        Shape3D first,
        in Transform3D firstTransform,
        Shape3D second,
        in Transform3D secondTransform,
        out ShapeContact contact)
    {
        Span<Vector3> simplex = stackalloc Vector3[4];
        if (!TryBuildIntersectionSimplex(first, firstTransform, second, secondTransform, simplex, out var simplexCount) ||
            simplexCount != 4)
        {
            contact = default;
            return false;
        }

        Span<Vector3> vertices = stackalloc Vector3[EpaVertexLimit];
        simplex.CopyTo(vertices);
        var vertexCount = simplexCount;
        Span<EpaFace> faces = stackalloc EpaFace[EpaFaceLimit];
        var faceCount = 0;
        if (!TryAddEpaFace(vertices, ref faces, ref faceCount, 0, 1, 2) ||
            !TryAddEpaFace(vertices, ref faces, ref faceCount, 0, 3, 1) ||
            !TryAddEpaFace(vertices, ref faces, ref faceCount, 0, 2, 3) ||
            !TryAddEpaFace(vertices, ref faces, ref faceCount, 1, 3, 2))
        {
            contact = default;
            return false;
        }

        Span<EpaEdge> boundary = stackalloc EpaEdge[EpaFaceLimit * 3];
        for (var iteration = 0; iteration < EpaIterationLimit; iteration++)
        {
            var closestFaceIndex = 0;
            for (var faceIndex = 1; faceIndex < faceCount; faceIndex++)
            {
                if (faces[faceIndex].Distance < faces[closestFaceIndex].Distance)
                {
                    closestFaceIndex = faceIndex;
                }
            }

            var closestFace = faces[closestFaceIndex];
            var support = Support(first, firstTransform, closestFace.Normal) -
                Support(second, secondTransform, -closestFace.Normal);
            if (!IsFinite(support))
            {
                contact = default;
                return false;
            }

            var supportDistance = Vector3.Dot(closestFace.Normal, support);
            if (supportDistance - closestFace.Distance <= EpaTolerance)
            {
                var normal = -closestFace.Normal;
                var firstPoint = Support(first, firstTransform, -normal);
                var secondPoint = Support(second, secondTransform, normal);
                contact = new ShapeContact(
                    (firstPoint + secondPoint) * 0.5f,
                    normal,
                    closestFace.Distance,
                    GetContactFeatureId(first, firstTransform, second, secondTransform, normal));
                return true;
            }

            if (vertexCount >= EpaVertexLimit)
            {
                break;
            }

            var newVertexIndex = vertexCount++;
            vertices[newVertexIndex] = support;
            var boundaryCount = 0;
            for (var faceIndex = faceCount - 1; faceIndex >= 0; faceIndex--)
            {
                var face = faces[faceIndex];
                if (Vector3.Dot(face.Normal, support - vertices[face.A]) <= EpaTolerance)
                {
                    continue;
                }

                if (!TryAddBoundaryEdge(boundary, ref boundaryCount, face.A, face.B) ||
                    !TryAddBoundaryEdge(boundary, ref boundaryCount, face.B, face.C) ||
                    !TryAddBoundaryEdge(boundary, ref boundaryCount, face.C, face.A))
                {
                    contact = default;
                    return false;
                }

                faces[faceIndex] = faces[--faceCount];
            }

            if (boundaryCount == 0)
            {
                break;
            }

            for (var edgeIndex = 0; edgeIndex < boundaryCount; edgeIndex++)
            {
                if (!TryAddEpaFace(vertices, ref faces, ref faceCount, boundary[edgeIndex].A, boundary[edgeIndex].B, newVertexIndex))
                {
                    contact = default;
                    return false;
                }
            }
        }

        contact = default;
        return false;
    }

    private static bool TryBuildIntersectionSimplex(
        Shape3D left,
        in Transform3D leftTransform,
        Shape3D right,
        in Transform3D rightTransform,
        Span<Vector3> simplex,
        out int count)
    {
        count = 0;
        var direction = rightTransform.Position - leftTransform.Position;
        if (direction.LengthSquared() <= Epsilon * Epsilon)
        {
            direction = Vector3.UnitX;
        }

        for (var iteration = 0; iteration < GjkIterationLimit; iteration++)
        {
            var directionLengthSquared = direction.LengthSquared();
            if (!float.IsFinite(directionLengthSquared) || directionLengthSquared <= Epsilon * Epsilon)
            {
                return count == 4;
            }

            var point = Support(left, leftTransform, direction) - Support(right, rightTransform, -direction);
            if (!IsFinite(point) || Vector3.Dot(point, direction) < 0f)
            {
                return false;
            }

            for (var simplexIndex = 0; simplexIndex < count; simplexIndex++)
            {
                if (Vector3.DistanceSquared(point, simplex[simplexIndex]) <= Epsilon * Epsilon)
                {
                    return false;
                }
            }

            for (var index = count; index > 0; index--)
            {
                simplex[index] = simplex[index - 1];
            }

            simplex[0] = point;
            count++;
            if (UpdateSimplex(simplex, ref count, ref direction))
            {
                return count == 4;
            }
        }

        return false;
    }

    private static bool TryAddEpaFace(
        Span<Vector3> vertices,
        ref Span<EpaFace> faces,
        ref int faceCount,
        int a,
        int b,
        int c)
    {
        if (faceCount >= faces.Length)
        {
            return false;
        }

        var normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
        var normalLengthSquared = normal.LengthSquared();
        if (!float.IsFinite(normalLengthSquared) || normalLengthSquared <= Epsilon * Epsilon)
        {
            return false;
        }

        normal /= MathF.Sqrt(normalLengthSquared);
        if (Vector3.Dot(normal, vertices[a]) < 0f)
        {
            (b, c) = (c, b);
            normal = -normal;
        }

        var distance = Vector3.Dot(normal, vertices[a]);
        if (!float.IsFinite(distance) || distance <= Epsilon)
        {
            return false;
        }

        faces[faceCount++] = new EpaFace(a, b, c, normal, distance);
        return true;
    }

    private static bool TryAddBoundaryEdge(Span<EpaEdge> edges, ref int edgeCount, int a, int b)
    {
        for (var edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
        {
            if (edges[edgeIndex].A == b && edges[edgeIndex].B == a)
            {
                edges[edgeIndex] = edges[--edgeCount];
                return true;
            }
        }

        if (edgeCount >= edges.Length)
        {
            return false;
        }

        edges[edgeCount++] = new EpaEdge(a, b);
        return true;
    }

    private static int GetContactFeatureId(
        Shape3D first,
        in Transform3D firstTransform,
        Shape3D second,
        in Transform3D secondTransform,
        in Vector3 normal)
    {
        var firstFeature = GetSupportFeatureId(first, firstTransform, -normal);
        var secondFeature = GetSupportFeatureId(second, secondTransform, normal);
        return firstFeature | (secondFeature << 8);
    }

    private static int GetSupportFeatureId(Shape3D shape, in Transform3D transform, in Vector3 direction)
    {
        var localDirection = Vector3.Transform(direction, Quaternion.Conjugate(transform.Rotation));
        return shape switch
        {
            BoxShape3D => 1 +
                (localDirection.X >= 0f ? 1 : 0) +
                (localDirection.Y >= 0f ? 2 : 0) +
                (localDirection.Z >= 0f ? 4 : 0),
            CapsuleShape3D => 16 + (localDirection.Y >= 0f ? 1 : 0),
            SphereShape3D => 0,
            _ => 0,
        };
    }

    public static bool Sweep(
        Shape3D movingShape,
        in Transform3D movingTransform,
        in Vector3 displacement,
        Shape3D targetShape,
        in Transform3D targetTransform,
        out ShapeSweepHit hit)
    {
        ArgumentNullException.ThrowIfNull(movingShape);
        ArgumentNullException.ThrowIfNull(targetShape);
        var movementLength = displacement.Length();
        if (!float.IsFinite(movementLength) || movementLength <= Epsilon)
        {
            hit = default;
            return false;
        }

        if (TrySweepAxisAlignedCapsuleBox(
                movingShape,
                movingTransform,
                displacement,
                targetShape,
                targetTransform,
                out var handled,
                out hit))
        {
            return true;
        }

        if (handled)
        {
            hit = default;
            return false;
        }

        if (!TryGetSweepSampleCount(
                movingShape,
                movingTransform,
                displacement,
                targetShape,
                targetTransform,
                out var sampleCount))
        {
            hit = default;
            return false;
        }

        var previousFraction = 0f;
        if (Overlaps(movingShape, movingTransform, targetShape, targetTransform))
        {
            hit = new ShapeSweepHit(
                0f,
                movingTransform.Position,
                GetSweepContactNormal(
                    movingShape,
                    movingTransform,
                    targetShape,
                    targetTransform,
                    -Vector3.Normalize(displacement)));
            return true;
        }

        for (var sample = 1; sample <= sampleCount; sample++)
        {
            var fraction = sample / (float)sampleCount;
            var sampleTransform = movingTransform.WithPosition(movingTransform.Position + displacement * fraction);
            if (!Overlaps(movingShape, sampleTransform, targetShape, targetTransform))
            {
                previousFraction = fraction;
                continue;
            }

            var minimum = previousFraction;
            var maximum = fraction;
            for (var iteration = 0; iteration < 16; iteration++)
            {
                var middle = (minimum + maximum) * 0.5f;
                var middleTransform = movingTransform.WithPosition(movingTransform.Position + displacement * middle);
                if (Overlaps(movingShape, middleTransform, targetShape, targetTransform))
                {
                    maximum = middle;
                }
                else
                {
                    minimum = middle;
                }
            }

            var contactTransform = movingTransform.WithPosition(movingTransform.Position + displacement * maximum);
            hit = new ShapeSweepHit(
                maximum,
                contactTransform.Position,
                GetSweepContactNormal(
                    movingShape,
                    contactTransform,
                    targetShape,
                    targetTransform,
                    -Vector3.Normalize(displacement)));
            return true;
        }

        hit = default;
        return false;
    }

    private static bool TryGetAxisAlignedCapsuleBoxContact(
        CapsuleShape3D capsule,
        in Transform3D capsuleTransform,
        BoxShape3D box,
        in Transform3D boxTransform,
        out ShapeContact contact)
    {
        if (!IsAxisAligned(capsuleTransform) || !IsAxisAligned(boxTransform))
        {
            contact = default;
            return false;
        }

        var radius = capsule.Radius * capsuleTransform.Scale;
        var halfCylinder = capsule.CylinderHeight * capsuleTransform.Scale * 0.5f;
        var boxHalf = box.Size * boxTransform.Scale * 0.5f;
        var boxMin = boxTransform.Position - boxHalf;
        var boxMax = boxTransform.Position + boxHalf;
        var capsuleMinY = capsuleTransform.Position.Y - halfCylinder - radius;
        var capsuleMaxY = capsuleTransform.Position.Y + halfCylinder + radius;
        var verticalOverlap = MathF.Min(capsuleMaxY, boxMax.Y) - MathF.Max(capsuleMinY, boxMin.Y);
        if (verticalOverlap < -Epsilon)
        {
            contact = default;
            return false;
        }

        var center = capsuleTransform.Position;
        var closestX = Math.Clamp(center.X, boxMin.X, boxMax.X);
        var closestZ = Math.Clamp(center.Z, boxMin.Z, boxMax.Z);
        var horizontalDelta = new Vector3(center.X - closestX, 0f, center.Z - closestZ);
        var horizontalDistance = horizontalDelta.Length();
        var horizontalPenetration = radius - horizontalDistance;
        Vector3 horizontalNormal;
        if (horizontalDistance > Epsilon)
        {
            horizontalNormal = horizontalDelta / horizontalDistance;
        }
        else
        {
            var left = center.X - boxMin.X;
            var right = boxMax.X - center.X;
            var front = center.Z - boxMin.Z;
            var back = boxMax.Z - center.Z;
            var minimum = MathF.Min(MathF.Min(left, right), MathF.Min(front, back));
            horizontalNormal = minimum == left ? -Vector3.UnitX :
                minimum == right ? Vector3.UnitX :
                minimum == front ? -Vector3.UnitZ : Vector3.UnitZ;
            horizontalPenetration = radius + minimum;
        }

        if (horizontalPenetration < -Epsilon)
        {
            contact = default;
            return false;
        }

        var verticalNormal = center.Y >= boxTransform.Position.Y ? Vector3.UnitY : -Vector3.UnitY;
        var verticalPenetration = verticalOverlap;
        var normal = horizontalPenetration >= 0f && horizontalPenetration < verticalPenetration
            ? horizontalNormal
            : verticalNormal;
        var penetration = normal.Y == 0f ? horizontalPenetration : verticalPenetration;
        if (penetration < -Epsilon)
        {
            contact = default;
            return false;
        }

        contact = CreateContact(capsule, capsuleTransform, box, boxTransform, normal, MathF.Max(0f, penetration));
        return true;
    }

    private static bool TrySweepAxisAlignedCapsuleBox(
        Shape3D movingShape,
        in Transform3D movingTransform,
        in Vector3 displacement,
        Shape3D targetShape,
        in Transform3D targetTransform,
        out bool handled,
        out ShapeSweepHit hit)
    {
        handled = false;
        hit = default;
        if (movingShape is not CapsuleShape3D capsule || targetShape is not BoxShape3D box ||
            !IsAxisAligned(movingTransform) || !IsAxisAligned(targetTransform) ||
            MathF.Abs(displacement.Y) > Epsilon)
        {
            return false;
        }

        handled = true;
        var radius = capsule.Radius * movingTransform.Scale;
        var halfCylinder = capsule.CylinderHeight * movingTransform.Scale * 0.5f;
        var boxHalf = box.Size * targetTransform.Scale * 0.5f;
        var boxMin = targetTransform.Position - boxHalf;
        var boxMax = targetTransform.Position + boxHalf;
        var capsuleMinY = movingTransform.Position.Y - halfCylinder - radius;
        var capsuleMaxY = movingTransform.Position.Y + halfCylinder + radius;
        if (capsuleMaxY < boxMin.Y - Epsilon || capsuleMinY > boxMax.Y + Epsilon)
        {
            return false;
        }

        var origin = movingTransform.Position;
        var direction = displacement;

        var closestX = Math.Clamp(origin.X, boxMin.X, boxMax.X);
        var closestZ = Math.Clamp(origin.Z, boxMin.Z, boxMax.Z);
        var initialDelta = new Vector2(origin.X - closestX, origin.Z - closestZ);
        var initialDistanceSquared = initialDelta.LengthSquared();
        if (!float.IsFinite(initialDistanceSquared))
        {
            return false;
        }

        var radiusSquared = radius * radius;
        var initialDelta3 = new Vector3(initialDelta.X, 0f, initialDelta.Y);
        var approachingInitialContact = Vector3.Dot(initialDelta3, direction) < -Epsilon;
        if (initialDistanceSquared < radiusSquared - Epsilon ||
            (initialDistanceSquared <= radiusSquared + Epsilon && approachingInitialContact))
        {
            Vector3 initialNormal;
            if (initialDistanceSquared > Epsilon * Epsilon)
            {
                initialNormal = new Vector3(initialDelta.X, 0f, initialDelta.Y);
                initialNormal = Vector3.Normalize(initialNormal);
            }
            else
            {
                var left = origin.X - boxMin.X;
                var right = boxMax.X - origin.X;
                var front = origin.Z - boxMin.Z;
                var back = boxMax.Z - origin.Z;
                var minimum = MathF.Min(MathF.Min(left, right), MathF.Min(front, back));
                initialNormal = minimum == left ? -Vector3.UnitX :
                    minimum == right ? Vector3.UnitX :
                    minimum == front ? -Vector3.UnitZ : Vector3.UnitZ;
            }

            hit = new ShapeSweepHit(0f, origin, initialNormal);
            return true;
        }

        var found = false;
        var fraction = 1f;
        var normal = Vector3.Zero;
        TrySweepCircleBoxFace(
            origin.X,
            direction.X,
            boxMin.X - radius,
            boxMin.Z,
            boxMax.Z,
            true,
            -Vector3.UnitX,
            origin.Z,
            direction.Z,
            ref found,
            ref fraction,
            ref normal);
        TrySweepCircleBoxFace(
            origin.X,
            direction.X,
            boxMax.X + radius,
            boxMin.Z,
            boxMax.Z,
            false,
            Vector3.UnitX,
            origin.Z,
            direction.Z,
            ref found,
            ref fraction,
            ref normal);
        TrySweepCircleBoxFace(
            origin.Z,
            direction.Z,
            boxMin.Z - radius,
            boxMin.X,
            boxMax.X,
            true,
            -Vector3.UnitZ,
            origin.X,
            direction.X,
            ref found,
            ref fraction,
            ref normal);
        TrySweepCircleBoxFace(
            origin.Z,
            direction.Z,
            boxMax.Z + radius,
            boxMin.X,
            boxMax.X,
            false,
            Vector3.UnitZ,
            origin.X,
            direction.X,
            ref found,
            ref fraction,
            ref normal);

        TrySweepCircleBoxCorner(
            origin,
            direction,
            new Vector2(boxMin.X, boxMin.Z),
            true,
            true,
            radius,
            ref found,
            ref fraction,
            ref normal);
        TrySweepCircleBoxCorner(
            origin,
            direction,
            new Vector2(boxMin.X, boxMax.Z),
            true,
            false,
            radius,
            ref found,
            ref fraction,
            ref normal);
        TrySweepCircleBoxCorner(
            origin,
            direction,
            new Vector2(boxMax.X, boxMin.Z),
            false,
            true,
            radius,
            ref found,
            ref fraction,
            ref normal);
        TrySweepCircleBoxCorner(
            origin,
            direction,
            new Vector2(boxMax.X, boxMax.Z),
            false,
            false,
            radius,
            ref found,
            ref fraction,
            ref normal);

        if (!found)
        {
            return false;
        }

        hit = new ShapeSweepHit(fraction, origin + direction * fraction, normal);
        return true;
    }

    private static void TrySweepCircleBoxFace(
        float origin,
        float direction,
        float plane,
        float rangeMinimum,
        float rangeMaximum,
        bool movingPositive,
        in Vector3 normal,
        float otherOrigin,
        float otherDirection,
        ref bool found,
        ref float bestFraction,
        ref Vector3 bestNormal)
    {
        if (movingPositive ? direction <= Epsilon : direction >= -Epsilon)
        {
            return;
        }

        if (movingPositive ? origin > plane + Epsilon : origin < plane - Epsilon)
        {
            return;
        }

        var fraction = (plane - origin) / direction;
        if (fraction < -Epsilon || fraction > 1f + Epsilon)
        {
            return;
        }

        var other = otherOrigin + otherDirection * fraction;
        if (other < rangeMinimum - Epsilon || other > rangeMaximum + Epsilon)
        {
            return;
        }

        ConsiderSweepCandidate(fraction, normal, ref found, ref bestFraction, ref bestNormal);
    }

    private static void TrySweepCircleBoxCorner(
        in Vector3 origin,
        in Vector3 direction,
        in Vector2 corner,
        bool isMinX,
        bool isMinZ,
        float radius,
        ref bool found,
        ref float bestFraction,
        ref Vector3 bestNormal)
    {
        var start = new Vector2(origin.X - corner.X, origin.Z - corner.Y);
        var travel = new Vector2(direction.X, direction.Z);
        var travelLengthSquared = travel.LengthSquared();
        if (travelLengthSquared <= Epsilon * Epsilon)
        {
            return;
        }

        var coefficientB = 2f * Vector2.Dot(start, travel);
        var coefficientC = Vector2.Dot(start, start) - radius * radius;
        var discriminant = coefficientB * coefficientB - 4f * travelLengthSquared * coefficientC;
        if (!float.IsFinite(discriminant) || discriminant < -Epsilon)
        {
            return;
        }

        var root = MathF.Sqrt(MathF.Max(0f, discriminant));
        var fraction = (-coefficientB - root) / (2f * travelLengthSquared);
        if (fraction < -Epsilon || fraction > 1f + Epsilon)
        {
            return;
        }

        var point = origin + direction * fraction;
        var pointXIsInCorner = isMinX
            ? point.X <= corner.X + Epsilon
            : point.X >= corner.X - Epsilon;
        var pointZIsInCorner = isMinZ
            ? point.Z <= corner.Y + Epsilon
            : point.Z >= corner.Y - Epsilon;
        if (!pointXIsInCorner || !pointZIsInCorner)
        {
            return;
        }

        var normal = new Vector3(point.X - corner.X, 0f, point.Z - corner.Y);
        var normalLengthSquared = normal.LengthSquared();
        if (normalLengthSquared <= Epsilon * Epsilon)
        {
            return;
        }

        normal /= MathF.Sqrt(normalLengthSquared);
        // 切线接触且沿切线方向移动时，距离会立即增大，不应在 t=0
        // 被误报为碰撞；只有朝圆角中心运动才阻挡扫掠。
        if (Vector3.Dot(normal, direction) >= -Epsilon)
        {
            return;
        }

        ConsiderSweepCandidate(fraction, normal, ref found, ref bestFraction, ref bestNormal);
    }

    private static void ConsiderSweepCandidate(
        float fraction,
        in Vector3 normal,
        ref bool found,
        ref float bestFraction,
        ref Vector3 bestNormal)
    {
        if (!float.IsFinite(fraction) || !IsFinite(normal))
        {
            return;
        }

        var clampedFraction = Math.Clamp(fraction, 0f, 1f);
        if (!found || clampedFraction < bestFraction - Epsilon)
        {
            found = true;
            bestFraction = clampedFraction;
            bestNormal = normal;
        }
    }

    private static bool IsAxisAligned(in Transform3D transform) =>
        MathF.Abs(MathF.Abs(transform.Rotation.W) - 1f) <= 1e-5f &&
        MathF.Abs(transform.Rotation.X) <= 1e-5f &&
        MathF.Abs(transform.Rotation.Y) <= 1e-5f &&
        MathF.Abs(transform.Rotation.Z) <= 1e-5f;

    /// <summary>判断一次扫掠是否能在固定的采样预算内完成。</summary>
    public static bool IsSweepWithinSampleBudget(
        Shape3D movingShape,
        in Transform3D movingTransform,
        in Vector3 displacement,
        Shape3D targetShape,
        in Transform3D targetTransform)
    {
        ArgumentNullException.ThrowIfNull(movingShape);
        ArgumentNullException.ThrowIfNull(targetShape);
        return TryGetSweepSampleCount(
            movingShape,
            movingTransform,
            displacement,
            targetShape,
            targetTransform,
            out _);
    }

    /// <summary>判断移动形状的包络球扫掠是否可能经过目标局部包围盒。</summary>
    public static bool MaySweepEncounter(
        Shape3D movingShape,
        in Transform3D movingTransform,
        in Vector3 displacement,
        Shape3D targetShape,
        in Transform3D targetTransform)
    {
        ArgumentNullException.ThrowIfNull(movingShape);
        ArgumentNullException.ThrowIfNull(targetShape);
        // 轴对齐胶囊-盒体水平路径已经由精确圆角扫掠处理；
        // 不再用包络球宽相位把切线擦过误报为未决碰撞并冻结角色。
        if (movingShape is CapsuleShape3D && targetShape is BoxShape3D &&
            IsAxisAligned(movingTransform) && IsAxisAligned(targetTransform) &&
            MathF.Abs(displacement.Y) <= Epsilon)
        {
            return false;
        }

        var movingBounds = movingShape.GetLocalBounds();
        var movingCenter = (movingBounds.Min + movingBounds.Max) * 0.5f;
        var movingRadius = (movingBounds.Max - movingBounds.Min).Length() * movingTransform.Scale * 0.5f;
        if (!float.IsFinite(movingRadius) || movingRadius < 0f)
        {
            return true;
        }

        var worldStart = movingTransform.Position +
            Vector3.Transform(movingCenter * movingTransform.Scale, movingTransform.Rotation);
        var inverseRotation = Quaternion.Conjugate(targetTransform.Rotation);
        var localStart = Vector3.Transform(worldStart - targetTransform.Position, inverseRotation) / targetTransform.Scale;
        var localEnd = Vector3.Transform(worldStart + displacement - targetTransform.Position, inverseRotation) / targetTransform.Scale;
        var localRadius = movingRadius / targetTransform.Scale;
        if (!float.IsFinite(localRadius) || !IsFinite(localStart) || !IsFinite(localEnd))
        {
            return true;
        }

        var targetBounds = targetShape.GetLocalBounds();
        var expansion = new Vector3(localRadius);
        return SegmentIntersectsAabb(localStart, localEnd, new Aabb3(targetBounds.Min - expansion, targetBounds.Max + expansion));
    }

    private static bool TryGetSweepSampleCount(
        Shape3D movingShape,
        in Transform3D movingTransform,
        in Vector3 displacement,
        Shape3D targetShape,
        in Transform3D targetTransform,
        out int sampleCount)
    {
        sampleCount = 0;
        var movementLength = displacement.Length();
        if (!float.IsFinite(movementLength))
        {
            return false;
        }

        if (movementLength <= Epsilon)
        {
            return true;
        }

        var stepLength = MathF.Max(
            GetMinimumFeatureSize(movingShape, movingTransform, targetShape, targetTransform) * 0.25f,
            Epsilon);
        var requiredSamples = MathF.Ceiling(movementLength / stepLength);
        if (!float.IsFinite(requiredSamples) || requiredSamples > MaximumSweepSampleCount)
        {
            return false;
        }

        sampleCount = Math.Max(1, (int)requiredSamples);
        return true;
    }

    private static bool SegmentIntersectsAabb(in Vector3 start, in Vector3 end, in Aabb3 bounds)
    {
        var direction = end - start;
        var entry = 0f;
        var exit = 1f;
        return IntersectSegmentAxis(start.X, direction.X, bounds.Min.X, bounds.Max.X, ref entry, ref exit) &&
            IntersectSegmentAxis(start.Y, direction.Y, bounds.Min.Y, bounds.Max.Y, ref entry, ref exit) &&
            IntersectSegmentAxis(start.Z, direction.Z, bounds.Min.Z, bounds.Max.Z, ref entry, ref exit);
    }

    private static bool IntersectSegmentAxis(
        float origin,
        float direction,
        float minimum,
        float maximum,
        ref float entry,
        ref float exit)
    {
        if (MathF.Abs(direction) <= Epsilon)
        {
            return origin >= minimum && origin <= maximum;
        }

        var inverseDirection = 1f / direction;
        var first = (minimum - origin) * inverseDirection;
        var second = (maximum - origin) * inverseDirection;
        if (first > second)
        {
            (first, second) = (second, first);
        }

        entry = MathF.Max(entry, first);
        exit = MathF.Min(exit, second);
        return entry <= exit;
    }

    private static bool RaycastBox(
        in Aabb3 bounds,
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        out ShapeRayHit hit)
    {
        var entry = 0f;
        var exit = maxDistance;
        var entryNormal = Vector3.Zero;
        if (!IntersectAxis(origin.X, direction.X, bounds.Min.X, bounds.Max.X, Vector3.UnitX, ref entry, ref exit, ref entryNormal) ||
            !IntersectAxis(origin.Y, direction.Y, bounds.Min.Y, bounds.Max.Y, Vector3.UnitY, ref entry, ref exit, ref entryNormal) ||
            !IntersectAxis(origin.Z, direction.Z, bounds.Min.Z, bounds.Max.Z, Vector3.UnitZ, ref entry, ref exit, ref entryNormal))
        {
            hit = default;
            return false;
        }

        if (entry <= 0f)
        {
            entry = 0f;
            entryNormal = -Vector3.Normalize(direction);
        }

        hit = new ShapeRayHit(entry, Vector3.Zero, entryNormal);
        return true;
    }

    private static float GetMinimumFeatureSize(
        Shape3D movingShape,
        in Transform3D movingTransform,
        Shape3D targetShape,
        in Transform3D targetTransform)
    {
        var movingSize = movingShape.GetLocalBounds().Max - movingShape.GetLocalBounds().Min;
        var targetSize = targetShape.GetLocalBounds().Max - targetShape.GetLocalBounds().Min;
        var movingMinimum = MathF.Min(movingSize.X, MathF.Min(movingSize.Y, movingSize.Z)) * movingTransform.Scale;
        var targetMinimum = MathF.Min(targetSize.X, MathF.Min(targetSize.Y, targetSize.Z)) * targetTransform.Scale;
        return MathF.Min(movingMinimum, targetMinimum);
    }

    private static Vector3 GetContactNormal(
        Shape3D targetShape,
        in Transform3D targetTransform,
        in Vector3 movingPosition,
        in Vector3 fallback)
    {
        var inverseRotation = Quaternion.Conjugate(targetTransform.Rotation);
        var localPosition = Vector3.Transform(movingPosition - targetTransform.Position, inverseRotation) / targetTransform.Scale;
        var localNormal = targetShape switch
        {
            BoxShape3D box => GetBoxContactNormal(box.GetLocalBounds(), localPosition),
            SphereShape3D sphere => localPosition.LengthSquared() > Epsilon * Epsilon
                ? Vector3.Normalize(localPosition)
                : Vector3.Transform(fallback, inverseRotation),
            CapsuleShape3D capsule => GetCapsuleContactNormal(capsule, localPosition),
            _ => throw new NotSupportedException($"不支持的 Shape3D 类型: {targetShape.GetType().FullName}"),
        };
        if (localNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            return fallback;
        }

        return Vector3.Normalize(Vector3.Transform(localNormal, targetTransform.Rotation));
    }

    /// <summary>
    /// 计算扫掠接触法线。胶囊不能只用中心点到盒体最近点：当胶囊轴线跨过盒体
    /// 的 Y 范围时，中心点会落在盒体上方，错误地产生“顶面+侧面”的对角法线，
    /// 使跳跃中的角色被误判为落地并丢失水平位移。
    /// </summary>
    private static Vector3 GetSweepContactNormal(
        Shape3D movingShape,
        in Transform3D movingTransform,
        Shape3D targetShape,
        in Transform3D targetTransform,
        in Vector3 fallback)
    {
        if (movingShape is not CapsuleShape3D capsule || targetShape is not BoxShape3D box)
        {
            return GetContactNormal(targetShape, targetTransform, movingTransform.Position, fallback);
        }

        GetCapsuleSegment(capsule, movingTransform, out var worldStart, out var worldEnd, out _);
        var inverseRotation = Quaternion.Conjugate(targetTransform.Rotation);
        var localStart = Vector3.Transform(worldStart - targetTransform.Position, inverseRotation) / targetTransform.Scale;
        var localEnd = Vector3.Transform(worldEnd - targetTransform.Position, inverseRotation) / targetTransform.Scale;
        var bounds = box.GetLocalBounds();

        // 轴线到 AABB 的距离沿线段参数是凸函数；三分搜索可稳定得到最近点，
        // 不引入堆分配，也能覆盖旋转后的胶囊和盒体。
        var minimum = 0f;
        var maximum = 1f;
        for (var iteration = 0; iteration < 18; iteration++)
        {
            var firstFraction = minimum + (maximum - minimum) / 3f;
            var secondFraction = maximum - (maximum - minimum) / 3f;
            var firstPoint = Vector3.Lerp(localStart, localEnd, firstFraction);
            var secondPoint = Vector3.Lerp(localStart, localEnd, secondFraction);
            var firstDistance = Vector3.DistanceSquared(firstPoint, Vector3.Clamp(firstPoint, bounds.Min, bounds.Max));
            var secondDistance = Vector3.DistanceSquared(secondPoint, Vector3.Clamp(secondPoint, bounds.Min, bounds.Max));
            if (firstDistance <= secondDistance)
            {
                maximum = secondFraction;
            }
            else
            {
                minimum = firstFraction;
            }
        }

        var closestPoint = Vector3.Lerp(localStart, localEnd, (minimum + maximum) * 0.5f);
        var localClosestOnBox = Vector3.Clamp(closestPoint, bounds.Min, bounds.Max);
        var localNormal = closestPoint - localClosestOnBox;
        if (localNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            localNormal = GetBoxContactNormal(bounds, closestPoint);
        }

        if (!IsFinite(localNormal) || localNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            return fallback;
        }

        return Vector3.Normalize(Vector3.Transform(Vector3.Normalize(localNormal), targetTransform.Rotation));
    }

    private static Vector3 GetBoxContactNormal(in Aabb3 bounds, in Vector3 position)
    {
        var closest = Vector3.Clamp(position, bounds.Min, bounds.Max);
        var normal = position - closest;
        if (normal.LengthSquared() > Epsilon * Epsilon)
        {
            return Vector3.Normalize(normal);
        }

        var toMinimum = position - bounds.Min;
        var toMaximum = bounds.Max - position;
        var x = MathF.Min(toMinimum.X, toMaximum.X);
        var y = MathF.Min(toMinimum.Y, toMaximum.Y);
        var z = MathF.Min(toMinimum.Z, toMaximum.Z);
        if (x <= y && x <= z)
        {
            return toMinimum.X <= toMaximum.X ? -Vector3.UnitX : Vector3.UnitX;
        }

        if (y <= z)
        {
            return toMinimum.Y <= toMaximum.Y ? -Vector3.UnitY : Vector3.UnitY;
        }

        return toMinimum.Z <= toMaximum.Z ? -Vector3.UnitZ : Vector3.UnitZ;
    }

    private static Vector3 GetCapsuleContactNormal(CapsuleShape3D capsule, in Vector3 position)
    {
        var halfHeight = capsule.CylinderHeight * 0.5f;
        var center = new Vector3(0f, Math.Clamp(position.Y, -halfHeight, halfHeight), 0f);
        var normal = position - center;
        return normal.LengthSquared() > Epsilon * Epsilon ? Vector3.Normalize(normal) : Vector3.Zero;
    }

    private static bool RaycastSphere(
        in Vector3 center,
        float radius,
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        out ShapeRayHit hit)
    {
        var offset = origin - center;
        var a = Vector3.Dot(direction, direction);
        var b = 2f * Vector3.Dot(offset, direction);
        var c = Vector3.Dot(offset, offset) - radius * radius;
        var discriminant = b * b - 4f * a * c;
        if (a <= Epsilon * Epsilon || discriminant < 0f)
        {
            hit = default;
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        var near = (-b - root) / (2f * a);
        var far = (-b + root) / (2f * a);
        var distance = near >= 0f ? near : far >= 0f ? far : -1f;
        if (distance < 0f || distance > maxDistance)
        {
            hit = default;
            return false;
        }

        var point = origin + direction * distance;
        var normal = point - center;
        hit = new ShapeRayHit(distance, point, normal.LengthSquared() <= Epsilon * Epsilon ? -Vector3.Normalize(direction) : Vector3.Normalize(normal));
        return true;
    }

    private static bool RaycastCapsule(
        CapsuleShape3D capsule,
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        out ShapeRayHit hit)
    {
        var halfHeight = capsule.CylinderHeight * 0.5f;
        var found = false;
        var best = default(ShapeRayHit);
        var bestDistance = maxDistance;

        var radialDirectionSquared = direction.X * direction.X + direction.Z * direction.Z;
        if (radialDirectionSquared > Epsilon * Epsilon)
        {
            var radialOriginSquared = origin.X * origin.X + origin.Z * origin.Z;
            var b = 2f * (origin.X * direction.X + origin.Z * direction.Z);
            var c = radialOriginSquared - capsule.Radius * capsule.Radius;
            var discriminant = b * b - 4f * radialDirectionSquared * c;
            if (discriminant >= 0f)
            {
                var root = MathF.Sqrt(discriminant);
                var near = (-b - root) / (2f * radialDirectionSquared);
                var far = (-b + root) / (2f * radialDirectionSquared);
                found |= TryCapsuleCylinderRoot(near, halfHeight, origin, direction, maxDistance, ref bestDistance, ref best);
                found |= TryCapsuleCylinderRoot(far, halfHeight, origin, direction, maxDistance, ref bestDistance, ref best);
            }
        }

        found |= TryCapsuleCap(
            new Vector3(0f, halfHeight, 0f),
            capsule.Radius,
            true,
            origin,
            direction,
            maxDistance,
            ref bestDistance,
            ref best);
        found |= TryCapsuleCap(
            new Vector3(0f, -halfHeight, 0f),
            capsule.Radius,
            false,
            origin,
            direction,
            maxDistance,
            ref bestDistance,
            ref best);
        hit = best;
        return found;
    }

    private static bool TryCapsuleCylinderRoot(
        float distance,
        float halfHeight,
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        ref float bestDistance,
        ref ShapeRayHit best)
    {
        if (distance < 0f || distance > maxDistance || distance > bestDistance)
        {
            return false;
        }

        var point = origin + direction * distance;
        if (point.Y < -halfHeight || point.Y > halfHeight)
        {
            return false;
        }

        var normal = new Vector3(point.X, 0f, point.Z);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
        {
            return false;
        }

        bestDistance = distance;
        best = new ShapeRayHit(distance, point, Vector3.Normalize(normal));
        return true;
    }

    private static bool TryCapsuleCap(
        in Vector3 center,
        float radius,
        bool isUpperCap,
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        ref float bestDistance,
        ref ShapeRayHit best)
    {
        var offset = origin - center;
        var directionLengthSquared = Vector3.Dot(direction, direction);
        var b = 2f * Vector3.Dot(offset, direction);
        var c = Vector3.Dot(offset, offset) - radius * radius;
        var discriminant = b * b - 4f * directionLengthSquared * c;
        if (directionLengthSquared <= Epsilon * Epsilon || discriminant < 0f)
        {
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        var near = (-b - root) / (2f * directionLengthSquared);
        var far = (-b + root) / (2f * directionLengthSquared);
        return TryCapsuleCapRoot(near, center, isUpperCap, origin, direction, maxDistance, ref bestDistance, ref best) |
            TryCapsuleCapRoot(far, center, isUpperCap, origin, direction, maxDistance, ref bestDistance, ref best);
    }

    private static bool TryCapsuleCapRoot(
        float distance,
        in Vector3 center,
        bool isUpperCap,
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        ref float bestDistance,
        ref ShapeRayHit best)
    {
        if (distance < 0f || distance > maxDistance || distance > bestDistance)
        {
            return false;
        }

        var point = origin + direction * distance;
        if (isUpperCap ? point.Y < center.Y : point.Y > center.Y)
        {
            return false;
        }

        bestDistance = distance;
        best = new ShapeRayHit(distance, point, Vector3.Normalize(point - center));
        return true;
    }

    private static bool IntersectAxis(
        float origin,
        float direction,
        float minimum,
        float maximum,
        in Vector3 axis,
        ref float entry,
        ref float exit,
        ref Vector3 entryNormal)
    {
        if (direction == 0f)
        {
            return origin >= minimum && origin <= maximum;
        }

        var inverse = 1f / direction;
        var near = (minimum - origin) * inverse;
        var far = (maximum - origin) * inverse;
        var nearNormal = direction > 0f ? -axis : axis;
        if (near > far)
        {
            (near, far) = (far, near);
        }

        if (near > entry)
        {
            entry = near;
            entryNormal = nearNormal;
        }

        exit = MathF.Min(exit, far);
        return exit >= entry;
    }

    private static Vector3 Support(Shape3D shape, in Transform3D transform, in Vector3 direction) => shape switch
    {
        BoxShape3D box => SupportBox(box, transform, direction),
        SphereShape3D sphere => SupportSphere(sphere, transform, direction),
        CapsuleShape3D capsule => SupportCapsule(capsule, transform, direction),
        _ => throw new NotSupportedException($"不支持的 Shape3D 类型: {shape.GetType().FullName}"),
    };

    private static Vector3 SupportBox(BoxShape3D box, in Transform3D transform, in Vector3 direction)
    {
        var localDirection = Vector3.Transform(direction, Quaternion.Conjugate(transform.Rotation));
        var extent = box.Size * 0.5f;
        var local = new Vector3(
            localDirection.X >= 0f ? extent.X : -extent.X,
            localDirection.Y >= 0f ? extent.Y : -extent.Y,
            localDirection.Z >= 0f ? extent.Z : -extent.Z);
        return transform.Position + Vector3.Transform(local * transform.Scale, transform.Rotation);
    }

    private static Vector3 SupportSphere(SphereShape3D sphere, in Transform3D transform, in Vector3 direction)
    {
        var normalized = NormalizeSupportDirection(direction);
        return transform.Position + normalized * (sphere.Radius * transform.Scale);
    }

    private static Vector3 SupportCapsule(CapsuleShape3D capsule, in Transform3D transform, in Vector3 direction)
    {
        var axis = Vector3.Transform(Vector3.UnitY, transform.Rotation);
        var center = transform.Position + axis * (Vector3.Dot(direction, axis) >= 0f ? 1f : -1f) *
            (capsule.CylinderHeight * 0.5f * transform.Scale);
        return center + NormalizeSupportDirection(direction) * (capsule.Radius * transform.Scale);
    }

    private static bool UpdateSimplex(Span<Vector3> simplex, ref int count, ref Vector3 direction)
    {
        var a = simplex[0];
        var ao = -a;
        if (count == 1)
        {
            direction = ao;
            return false;
        }

        var b = simplex[1];
        var ab = b - a;
        if (count == 2)
        {
            if (Vector3.Dot(ab, ao) > 0f)
            {
                direction = TripleCross(ab, ao, ab);
                if (direction.LengthSquared() <= Epsilon * Epsilon)
                {
                    direction = Perpendicular(ab);
                }
            }
            else
            {
                count = 1;
                direction = ao;
            }

            return false;
        }

        var c = simplex[2];
        var ac = c - a;
        var abc = Vector3.Cross(ab, ac);
        if (count == 3)
        {
            if (Vector3.Dot(Vector3.Cross(abc, ac), ao) > 0f)
            {
                if (Vector3.Dot(ac, ao) > 0f)
                {
                    simplex[1] = c;
                    count = 2;
                    direction = TripleCross(ac, ao, ac);
                }
                else
                {
                    simplex[1] = b;
                    count = 2;
                    direction = TripleCross(ab, ao, ab);
                }

                return false;
            }

            if (Vector3.Dot(Vector3.Cross(ab, abc), ao) > 0f)
            {
                simplex[1] = b;
                count = 2;
                direction = TripleCross(ab, ao, ab);
                return false;
            }

            if (Vector3.Dot(abc, ao) > 0f)
            {
                direction = abc;
            }
            else
            {
                simplex[1] = c;
                simplex[2] = b;
                direction = -abc;
            }

            return false;
        }

        var d = simplex[3];
        if (OutsideFace(a, b, c, d, ao, out var normal))
        {
            simplex[1] = b;
            simplex[2] = c;
            count = 3;
            direction = normal;
            return false;
        }

        if (OutsideFace(a, c, d, b, ao, out normal))
        {
            simplex[1] = c;
            simplex[2] = d;
            count = 3;
            direction = normal;
            return false;
        }

        if (OutsideFace(a, d, b, c, ao, out normal))
        {
            simplex[1] = d;
            simplex[2] = b;
            count = 3;
            direction = normal;
            return false;
        }

        return true;
    }

    private static bool OutsideFace(
        in Vector3 a,
        in Vector3 b,
        in Vector3 c,
        in Vector3 opposite,
        in Vector3 ao,
        out Vector3 normal)
    {
        normal = Vector3.Cross(b - a, c - a);
        if (Vector3.Dot(normal, opposite - a) > 0f)
        {
            normal = -normal;
        }

        return Vector3.Dot(normal, ao) > 0f;
    }

    private static Vector3 TripleCross(in Vector3 left, in Vector3 middle, in Vector3 right) =>
        Vector3.Cross(Vector3.Cross(left, middle), right);

    private static Vector3 Perpendicular(in Vector3 value) =>
        MathF.Abs(value.X) < MathF.Abs(value.Y)
            ? Vector3.Cross(value, Vector3.UnitX)
            : Vector3.Cross(value, Vector3.UnitY);

    private static Vector3 NormalizeSupportDirection(in Vector3 direction)
    {
        var lengthSquared = direction.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 0f
            ? direction / MathF.Sqrt(lengthSquared)
            : Vector3.UnitX;
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal readonly record struct ShapeRayHit(float Distance, Vector3 Position, Vector3 Normal);

internal readonly record struct ShapeSweepHit(float Fraction, Vector3 Position, Vector3 Normal);

internal readonly record struct ShapeContact(Vector3 Position, Vector3 Normal, float Penetration, int FeatureId = 0);

internal readonly record struct EpaFace(int A, int B, int C, Vector3 Normal, float Distance);

internal readonly record struct EpaEdge(int A, int B);
