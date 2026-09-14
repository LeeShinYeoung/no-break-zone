using System;
using System.Collections.Generic;
using PugTilemap;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace NoBreakZone.EditorTools
{
    /// <summary>
    /// What a change to the switched-on pylons costs, measured rather than guessed. Run on demand
    /// with `verify.ps1 -Perf`; it is not part of the verification ladder.
    ///
    /// Whenever the set of switched-on pylons changes, NoBreakZonePylonRegistrySystem drops the
    /// judged tag from every entity in the world, and NoBreakZoneProtectionSystem then judges each of
    /// them again, one structural change per entity. That set changes on more than a player's toggle:
    /// the server unloads chunks far from every player (UnloadToSerializeWorldSystem), and a client
    /// only holds ghosts within about 22x14 tiles of its player (SetGhostRelevancySetSystem), so a
    /// pylon leaving or entering either world counts as well. This puts numbers on how that grows
    /// with the size of the world and the number of pylons, so that a fix can be judged by what it
    /// actually saves.
    ///
    /// NOT A GATE. The only checks are sanity checks that the world was judged at all. Every timing
    /// is printed and none is compared against a limit.
    ///
    /// READ THE MILLISECONDS AS RATIOS. This is the editor in batch mode, with the Entities safety
    /// checks on and a world whose entities fall into a handful of archetypes, so the absolute
    /// figures say little about a player build. What carries over is how the cost grows with objects
    /// and pylons, and how many re-evaluations a single event sets off.
    /// </summary>
    internal static class VerifyTogglePerformance
    {
        private static readonly int[] ObjectCounts = { 1000, 5000, 20000 };
        private static readonly int[] PylonCounts = { 1, 10, 100 };

        // A square of the default diameter is 21 tiles wide, so at this spacing neighbouring squares
        // meet edge to edge, the way a player covering a large base would lay them out.
        private const int PylonSpacing = 21;

        // Frames run after each event. An event settles within two or three; the rest show that it
        // stays settled.
        private const int FramesPerEvent = 8;

        // The registry writes this line each time it drops the judged tag from the world, so counting
        // it counts re-evaluations without a test seam in shipping code.
        private const string ReevaluationLine = "[NoBreakZone] active pylons:";
        private static int _reevaluations;

        private sealed class Harness
        {
            public NoBreakZonePylonRegistrySystem Registry;
            public NoBreakZoneProtectionSystem Protection;
            public EntityQuery Waiting;
            public int Objects;
            public int Pylons;
        }

        internal static void Run(VerifyReport report)
        {
            Dictionary<ObjectDataCD, ObjectInfo> savedDatabase = PugDatabase.objectsByType;
            Application.logMessageReceived += CountReevaluations;

            try
            {
                VerifyProtectionBehaviour.InstallFakeDatabase();

                Debug.Log("[NBZ] perf: editor batch mode - compare rows with each other, not with a game build");
                Debug.Log("[NBZ] perf: time = registry + protection updates; frames = frames that did any work");

                // JIT and first-use costs land on whichever run goes first. Pay them on a small world
                // nobody reads.
                RunWorld(report, 1000, 1, print: false);

                foreach (int objects in ObjectCounts)
                {
                    foreach (int pylons in PylonCounts)
                    {
                        RunWorld(report, objects, pylons, print: true);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Check(false, "the performance harness ran without throwing");
            }
            finally
            {
                Application.logMessageReceived -= CountReevaluations;
                PugDatabase.objectsByType = savedDatabase;
            }
        }

        /// Builds one world and puts one pylon, the subject, through everything that can change the
        /// set of switched-on pylons.
        private static void RunWorld(VerifyReport report, int objects, int pylons, bool print)
        {
            var world = new World("NoBreakZoneVerifyPerf");

            try
            {
                var h = new Harness
                {
                    Registry = world.GetOrCreateSystemManaged<NoBreakZonePylonRegistrySystem>(),
                    Protection = world.GetOrCreateSystemManaged<NoBreakZoneProtectionSystem>(),
                    Objects = objects,
                    Pylons = pylons,
                };

                if (!VerifyProtectionBehaviour.TrySetPylonId(
                        h.Registry, (ObjectID)VerifyProtectionBehaviour.PylonId))
                {
                    report.Check(false, "the registry still has a _pylonObjectID field to seed");
                    return;
                }

                EntityManager em = world.EntityManager;

                // NoBreakZoneProtectionSystem's candidate query, so a frame can say how many entities
                // it was about to judge.
                h.Waiting = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<HealthCD>(),
                        ComponentType.ReadOnly<ObjectDataCD>(),
                        ComponentType.ReadOnly<LocalTransform>(),
                    },
                    None = new[]
                    {
                        ComponentType.ReadOnly<NoBreakZoneEvaluatedCD>(),
                        ComponentType.ReadOnly<NoBreakZonePylonCD>(),
                    },
                });
                EntityQuery claimed = em.CreateEntityQuery(ComponentType.ReadOnly<NoBreakZoneProtectedCD>());

                Entity subject = PlacePylons(em, pylons);
                PlaceObjects(em, objects, pylons);

                // Everything the first frames of a world do once: classify every entity, judge the ones
                // the other pylons cover. A save that has been running a while is past this.
                Measure(h, "setup", print: false);

                // The subject was placed switched off and has never been on, like a pylon just put
                // down. The first switch-on is the one that gives it its own guards.
                SetVariation(em, subject, NoBreakZonePylonGraphics.VariationOn);
                Measure(h, "first switch-on", print);

                if (print)
                {
                    report.Check(h.Waiting.CalculateEntityCount() == 0 && !claimed.IsEmpty,
                        $"perf world objects={objects} pylons={pylons} was judged and has something protected");
                }

                SetVariation(em, subject, NoBreakZonePylonGraphics.VariationOff);
                Measure(h, "switch-off", print);

                SetVariation(em, subject, NoBreakZonePylonGraphics.VariationOn);
                Measure(h, "switch-on again", print);

                // What the server does to a pylon when no player is near its chunk, and what a client
                // sees when the pylon leaves its relevancy rectangle: the entity is simply gone.
                em.DestroyEntity(subject);
                Measure(h, "pylon unloads", print);

                // And the reverse: a new entity, unclassified and without guards, where the old one
                // stood. In the game the objects around it come back with it; here only the pylon does.
                subject = VerifyProtectionBehaviour.MakeObject(
                    em, VerifyProtectionBehaviour.PylonId, 0, 0,
                    variation: NoBreakZonePylonGraphics.VariationOn);
                Measure(h, "pylon loads back", print);

                // Nothing changes. What every other frame of play costs.
                Measure(h, "idle", print);
            }
            finally
            {
                world.Dispose();
            }
        }

        /// Runs FramesPerEvent frames after an event and prints what they cost.
        private static void Measure(Harness h, string eventName, bool print)
        {
            int reevaluationsBefore = _reevaluations;
            int busyFrames = 0;
            int judged = 0;
            double worstMs = 0;
            double busyMs = 0;

            for (int i = 0; i < FramesPerEvent; i++)
            {
                int reevaluationsAtStart = _reevaluations;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                h.Registry.Update();
                watch.Stop();
                double ms = watch.Elapsed.TotalMilliseconds;

                // Counted between the two systems and outside the timing: the registry has just
                // decided whether to drop the tag, and the protection system is about to judge what it
                // dropped. With no pylon switched on the protection system leaves candidates alone, so
                // they only count when there is a square to judge them against.
                int candidates = h.Registry.Positions.Length > 0 ? h.Waiting.CalculateEntityCount() : 0;

                watch.Restart();
                h.Protection.Update();
                watch.Stop();
                ms += watch.Elapsed.TotalMilliseconds;

                worstMs = Math.Max(worstMs, ms);

                if (candidates > 0 || _reevaluations != reevaluationsAtStart)
                {
                    busyFrames++;
                    judged += candidates;
                    busyMs += ms;
                }
            }

            if (!print)
            {
                return;
            }

            Debug.Log($"[NBZ] perf: objects={h.Objects,6} pylons={h.Pylons,4}  {eventName,-16}"
                      + $"  re-evaluations={_reevaluations - reevaluationsBefore}"
                      + $"  re-judged={judged,6}  frames={busyFrames}"
                      + $"  worst={worstMs,7:F1} ms  total={busyMs,7:F1} ms");
        }

        /// Pylons on a square grid, PylonSpacing apart. The first is the subject; every other one is
        /// switched on.
        private static Entity PlacePylons(EntityManager em, int pylons)
        {
            int columns = Columns(pylons);
            Entity subject = Entity.Null;

            for (int i = 0; i < pylons; i++)
            {
                int variation = i == 0
                    ? NoBreakZonePylonGraphics.VariationOff
                    : NoBreakZonePylonGraphics.VariationOn;

                Entity pylon = VerifyProtectionBehaviour.MakeObject(
                    em, VerifyProtectionBehaviour.PylonId,
                    (i % columns) * PylonSpacing, (i / columns) * PylonSpacing, variation);

                if (i == 0)
                {
                    subject = pylon;
                }
            }

            return subject;
        }

        /// Chests, floors and ore scattered over the pylons' squares and a margin beyond them, so some
        /// of each kind lands outside every square. Chests and floors are protected inside a square;
        /// ore never is.
        ///
        /// Created from an archetype, a kind at a time, rather than with three structural changes per
        /// entity, so that building the world does not take longer than measuring it.
        private static void PlaceObjects(EntityManager em, int objects, int pylons)
        {
            int columns = Columns(pylons);
            int rows = (pylons + columns - 1) / columns;
            int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter);

            int2 covered = new int2(
                (columns - 1) * PylonSpacing + 2 * radius + 1,
                (rows - 1) * PylonSpacing + 2 * radius + 1);
            int2 margin = covered / 4;
            int2 min = -radius - margin;
            int2 max = new int2((columns - 1) * PylonSpacing, (rows - 1) * PylonSpacing) + radius + margin;

            // Fixed, so every run builds the same world and runs can be compared.
            var random = new Random(0x4E425A);

            EntityArchetype placeable = em.CreateArchetype(
                ComponentType.ReadWrite<ObjectDataCD>(),
                ComponentType.ReadWrite<LocalTransform>(),
                ComponentType.ReadWrite<HealthCD>());
            EntityArchetype tile = em.CreateArchetype(
                ComponentType.ReadWrite<ObjectDataCD>(),
                ComponentType.ReadWrite<LocalTransform>(),
                ComponentType.ReadWrite<HealthCD>(),
                ComponentType.ReadWrite<TileCD>());

            int chests = objects * 4 / 10;
            int floors = objects * 3 / 10;
            int ores = objects - chests - floors;

            Spawn(em, placeable, chests, VerifyProtectionBehaviour.ChestId, null, ref random, min, max);
            Spawn(em, tile, floors, VerifyProtectionBehaviour.FloorId, TileType.floor, ref random, min, max);
            Spawn(em, tile, ores, VerifyProtectionBehaviour.OreId, TileType.ore, ref random, min, max);
        }

        private static void Spawn(EntityManager em, EntityArchetype archetype, int count, int id,
                                  TileType? tileType, ref Random random, int2 min, int2 max)
        {
            using var entities = new NativeArray<Entity>(count, Allocator.Temp);
            em.CreateEntity(archetype, entities);

            for (int i = 0; i < count; i++)
            {
                Entity entity = entities[i];
                int2 tilePosition = random.NextInt2(min, max + 1);

                em.SetComponentData(entity, new ObjectDataCD
                {
                    objectID = (ObjectID)id,
                    amount = 1,
                    variation = 0,
                });
                em.SetComponentData(entity,
                    LocalTransform.FromPosition(new float3(tilePosition.x, 0f, tilePosition.y)));
                em.SetComponentData(entity, new HealthCD { health = 1, maxHealth = 1 });

                if (tileType.HasValue)
                {
                    em.SetComponentData(entity, new TileCD { tileset = 0, tileType = tileType.Value });
                }
            }
        }

        private static void SetVariation(EntityManager em, Entity pylon, int variation)
        {
            em.SetComponentData(pylon, new ObjectDataCD
            {
                objectID = (ObjectID)VerifyProtectionBehaviour.PylonId,
                amount = 1,
                variation = variation,
            });
        }

        private static int Columns(int pylons)
        {
            return (int)math.ceil(math.sqrt(pylons));
        }

        private static void CountReevaluations(string message, string stackTrace, LogType type)
        {
            if (message != null && message.StartsWith(ReevaluationLine, StringComparison.Ordinal))
            {
                _reevaluations++;
            }
        }
    }
}
