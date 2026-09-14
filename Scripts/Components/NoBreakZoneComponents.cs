using Unity.Entities;

// Bookkeeping tags. Neither is a ghost component: runtime-added components are never replicated
// (NetCode freezes a ghost's component set at bake time — research.md chapter 9), so each world
// adds its own copy and both worlds run NoBreakZoneProtectionSystem to keep them in step.

// "This entity has already been through the discriminator." design.md §9 requires that we do not
// re-walk the world every frame; with this tag the query only ever sees entities we have not judged
// yet. Cleared in bulk when the set of active pylons changes, which is the only event that can
// change an answer (stage 4).
public struct NoBreakZoneEvaluatedCD : IComponentData
{
}

// "We are the ones who made this entity indestructible." Needed because some objects ship with
// IndestructibleCD already enabled; without this tag, switching a pylon off would strip protection
// from things the game intended to be permanent. design.md §9: "it must be done in a way that can
// be undone".
public struct NoBreakZoneProtectedCD : IComponentData
{
}

// "This entity is one of our pylons." Applied by NoBreakZonePylonRegistrySystem after matching
// ObjectDataCD.objectID, so the per-frame cost of finding pylons is a query over the pylons
// themselves rather than over every object in the world.
public struct NoBreakZonePylonCD : IComponentData
{
}

// "We have already asked whether this entity is a pylon." The answer cannot change during an
// entity's life, so asking once and tagging keeps the discovery query down to entities that have
// just streamed in. Separate from NoBreakZoneEvaluatedCD because the protection system only ever
// tags things that passed its own narrower query, and anything it skipped would otherwise be
// rescanned by the registry every frame.
public struct NoBreakZonePylonScannedCD : IComponentData
{
}
