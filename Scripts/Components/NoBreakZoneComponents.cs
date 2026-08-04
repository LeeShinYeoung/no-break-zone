using Unity.Entities;

// Bookkeeping tags. Neither is a ghost component: runtime-added components are never replicated
// (NetCode freezes a ghost's component set at bake time — research.md 9장), so each world adds its
// own copy and both worlds run NoBreakZoneProtectionSystem to keep them in step.

// "This entity has already been through the discriminator." 기획서 §9 requires that we do not re-walk
// the world every frame; with this tag the query only ever sees entities we have not judged yet.
// Cleared in bulk when the set of active pylons changes, which is the only event that can change an
// answer (4단계).
public struct NoBreakZoneEvaluatedCD : IComponentData
{
}

// "We are the ones who made this entity indestructible." Needed because some objects ship with
// IndestructibleCD already enabled; without this tag, switching a pylon off would strip protection
// from things the game intended to be permanent. 기획서 §9: "되돌릴 수 있는 방식이어야 한다".
public struct NoBreakZoneProtectedCD : IComponentData
{
}
