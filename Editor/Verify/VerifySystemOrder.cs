using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace NoBreakZone.EditorTools
{
    /// <summary>
    /// Proves the mod's systems run where they say they do.
    ///
    /// This exists because the tile fix is ENTIRELY a question of ordering. A tile has no entity
    /// until it is hit; the one TileDamageSystem creates appears at the next frame's
    /// BeginSimulationEntityCommandBufferSystem and is destroyed inside PredictedSimulationSystemGroup
    /// in that same frame. Land outside that window and the mod silently does nothing — which is
    /// exactly what an explosion used to do to a protected floor.
    ///
    /// Two independent halves, because each catches what the other cannot:
    ///   - the attribute graph, read straight off the types, so a game update that moves one of the
    ///     game's own systems is caught here rather than in play;
    ///   - Unity's real sorter, run over a real World, so a mistake in our own attributes is caught
    ///     even when they look right.
    /// </summary>
    internal static class VerifySystemOrder
    {
        internal static void Run(VerifyReport report)
        {
            AttributeGraph(report);
            RealSorter(report);
        }

        // --------------------------------------------------------------------------- attribute graph

        private static void AttributeGraph(VerifyReport report)
        {
            foreach (Type ours in new[]
                     {
                         typeof(NoBreakZonePylonRegistrySystem),
                         typeof(NoBreakZoneProtectionSystem),
                     })
            {
                UpdateInGroupAttribute group = ours
                    .GetCustomAttributes(typeof(UpdateInGroupAttribute), true)
                    .Cast<UpdateInGroupAttribute>()
                    .FirstOrDefault();

                report.Check(group != null && group.GroupType == typeof(SimulationSystemGroup),
                    $"{ours.Name} updates in SimulationSystemGroup");
                report.Check(group != null && group.OrderFirst,
                    $"{ours.Name} is OrderFirst — constraints are dropped across sorting buckets");

                report.Check(UpdatesAfter(ours, typeof(BeginSimulationEntityCommandBufferSystem)),
                    $"{ours.Name} runs after the command buffer that creates the tile damage entity");
                report.Check(UpdatesAfter(ours, typeof(GhostSimulationSystemGroup)),
                    $"{ours.Name} runs after ghost snapshots land, so a pylon's variation is current");
                report.Check(UpdatesBefore(ours, typeof(PredictedSimulationSystemGroup)),
                    $"{ours.Name} runs before any predicted damage is applied");
            }

            report.Check(
                UpdatesBefore(typeof(NoBreakZonePylonRegistrySystem), typeof(NoBreakZoneProtectionSystem)),
                "the registry publishes positions before the protection system reads them");

            // The game's half of the pipeline. If an update moves any of these, the reasoning above
            // stops holding and we want to hear about it from a script, not from a player.
            foreach (string name in new[]
                     {
                         "TileDamageSystem",
                         "ExplosionDamageSystem",
                         "InitialHealthChangeSystem",
                         "UpdateHealthFromBufferSystem",
                         "SetEntitiesDestroyedSystem",
                     })
            {
                Type game = FindGameSystem(name);
                if (game == null)
                {
                    report.Check(false, $"the game still has a system called {name}");
                    continue;
                }

                report.Check(IsInsideGroup(game, typeof(PredictedSimulationSystemGroup)),
                    $"{name} is still inside PredictedSimulationSystemGroup");
            }

            HealthFloorOrder(report);

            // Why the mod does not simply join BeforePredictedSimulationSystemGroup, where the game's
            // own ImmunityZoneSystem sits: nothing orders that group against the command buffer, so
            // the sorter's tie-break decides and the fix becomes a coin flip. Asserted so that if a
            // future version adds the edge, we find out and can simplify.
            Type beforePredicted = FindGameSystem("BeforePredictedSimulationSystemGroup");
            if (beforePredicted != null)
            {
                report.Check(!UpdatesAfter(beforePredicted, typeof(BeginSimulationEntityCommandBufferSystem)),
                    "BeforePredictedSimulationSystemGroup still has no edge to the command buffer "
                    + "(if this fails, the explicit constraints above can collapse into that group)");
            }
        }

        /// NoBreakZoneHealthFloorSystem has to land in one exact slot, and outside it the fix it
        /// carries is worth nothing.
        ///
        /// It restores protected objects that have been mined to zero, so that
        /// SetEntitiesDestroyedSystem — which destroys anything at zero health once the guard comes
        /// off — never finds one. Too early and it reads health the game has not finished subtracting
        /// from; too late and the thing is already dead. Between the health group and the destroy
        /// gate is the only place that works.
        ///
        /// Pinned as tightly as the registry and protection systems are, and for the same reason:
        /// this project has already lost play sessions to a frame-order assumption that read
        /// correctly and was wrong (research.md chapter 21).
        private static void HealthFloorOrder(VerifyReport report)
        {
            Type ours = typeof(NoBreakZoneHealthFloorSystem);

            report.Check(IsInsideGroup(ours, typeof(PredictedSimulationSystemGroup)),
                "the health floor updates inside PredictedSimulationSystemGroup, where the damage "
                + "it is undoing is applied");

            Type healthGroup = FindGameSystem("UpdateHealthSystemGroup");
            Type destroyGate = FindGameSystem("SetEntitiesDestroyedSystem");

            if (healthGroup == null || destroyGate == null)
            {
                report.Check(false,
                    "the game still has UpdateHealthSystemGroup and SetEntitiesDestroyedSystem to "
                    + "sit between");
                return;
            }

            report.Check(UpdatesAfter(ours, healthGroup),
                "the health floor runs AFTER health is applied — earlier and it reads a number the "
                + "game has not finished subtracting from");

            report.Check(UpdatesBefore(ours, destroyGate),
                "and BEFORE the destroy gate — later and the base is already rubble. This is the "
                + "check the whole fix rests on");
        }

        private static bool UpdatesAfter(Type system, Type other)
        {
            return system.GetCustomAttributes(typeof(UpdateAfterAttribute), true)
                .Cast<UpdateAfterAttribute>()
                .Any(a => a.SystemType == other);
        }

        private static bool UpdatesBefore(Type system, Type other)
        {
            return system.GetCustomAttributes(typeof(UpdateBeforeAttribute), true)
                .Cast<UpdateBeforeAttribute>()
                .Any(a => a.SystemType == other);
        }

        /// Walks UpdateInGroup upwards until it finds the group or runs out of parents.
        private static bool IsInsideGroup(Type system, Type group)
        {
            for (int depth = 0; system != null && depth < 8; depth++)
            {
                UpdateInGroupAttribute attribute = system
                    .GetCustomAttributes(typeof(UpdateInGroupAttribute), true)
                    .Cast<UpdateInGroupAttribute>()
                    .FirstOrDefault();

                if (attribute == null)
                {
                    return false;
                }

                if (attribute.GroupType == group)
                {
                    return true;
                }

                system = attribute.GroupType;
            }

            return false;
        }

        /// The game's systems live in the global namespace of assemblies this one references, so a
        /// plain Type.GetType finds nothing without an assembly-qualified name. Searching by name is
        /// good enough and stays readable when an assembly is renamed.
        private static Type FindGameSystem(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found;
                try
                {
                    found = assembly.GetType(name, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------------------ real sorter

        /// Runs Unity's own sorter over the systems that matter and reads the order back out.
        ///
        /// Only groups and a command buffer system are instantiated. The game's damage systems are
        /// deliberately left out: their OnCreate wants a database, a tilemap and a NetCode world, and
        /// their placement is already covered by the attribute half above.
        private static void RealSorter(VerifyReport report)
        {
            var world = new World("NoBreakZoneVerifyOrder");
            try
            {
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, new[]
                {
                    typeof(BeginSimulationEntityCommandBufferSystem),
                    typeof(GhostSimulationSystemGroup),
                    typeof(PredictedSimulationSystemGroup),
                    typeof(NoBreakZonePylonRegistrySystem),
                    typeof(NoBreakZoneProtectionSystem),
                });

                var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();
                simulation.SortSystems();

                NativeList<SystemHandle> sorted = simulation.GetAllSystems(Allocator.Temp);
                var order = new List<SystemHandle>(sorted.Length);
                for (int i = 0; i < sorted.Length; i++)
                {
                    order.Add(sorted[i]);
                }

                sorted.Dispose();

                int ecb = order.IndexOf(world.GetExistingSystemManaged<BeginSimulationEntityCommandBufferSystem>().SystemHandle);
                int registry = order.IndexOf(world.GetExistingSystemManaged<NoBreakZonePylonRegistrySystem>().SystemHandle);
                int protection = order.IndexOf(world.GetExistingSystemManaged<NoBreakZoneProtectionSystem>().SystemHandle);
                int predicted = order.IndexOf(world.GetExistingSystemManaged<PredictedSimulationSystemGroup>().SystemHandle);

                report.Check(ecb >= 0 && registry >= 0 && protection >= 0 && predicted >= 0,
                    "all four systems are in the sorted simulation group");

                report.Check(ecb < registry,
                    $"sorted: command buffer ({ecb}) before the registry ({registry}) — "
                    + "the tile damage entity exists by the time we look");
                report.Check(registry < protection,
                    $"sorted: registry ({registry}) before protection ({protection})");
                report.Check(protection < predicted,
                    $"sorted: protection ({protection}) before predicted simulation ({predicted}) — "
                    + "this is the check the explosion bug comes down to");
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}
