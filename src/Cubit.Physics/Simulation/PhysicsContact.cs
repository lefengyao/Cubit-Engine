using System.Numerics;
using Cubit.Physics.Scene;

namespace Cubit.Physics.Simulation;

/// <summary>稳定排序后交给线性求解器的接触快照。</summary>
internal readonly record struct PhysicsContact(
    CollisionObject3D First,
    CollisionObject3D Second,
    Vector3 Position,
    Vector3 Normal,
    float Penetration,
    long FirstColliderId,
    long SecondColliderId,
    int FeatureId);
