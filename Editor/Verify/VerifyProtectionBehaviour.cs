using System;
using System.Collections.Generic;
using System.Reflection;
using PugTilemap;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace NoBreakZone.EditorTools
{
    /// <summary>
    /// Drives the real protection system over a hand-built World and checks what it did.
    ///
    /// This is the only automated check that exercises the discriminator, the footprint walk and the
    /// release path together. It cannot prove the game hands the system this state — see the note on
    /// CliVerify — but it does prove that when it is handed it, the answers are right, and it catches
    /// the whole class of "the query no longer matches" bug that cost several play sessions before.
    /// </summary>
    internal static class VerifyProtectionBehaviour
    {
        // Ids outside anything the game defines. Nothing looks them up except the fake database
        // installed below, so they only have to be distinct.
        private const int PylonId = 9001;
        private const int ChestId = 9002;
        private const int FloorId = 9003;
        private const int OreId = 9004;
        private const int WallId = 9005;

        internal static void Run(VerifyReport report)
        {
            Dictionary<ObjectDataCD, ObjectInfo> savedDatabase = PugDatabase.objectsByType;
            var world = new World("NoBreakZoneVerifyBehaviour");

            try
            {
                InstallFakeDatabase();

                var registry = world.GetOrCreateSystemManaged<NoBreakZonePylonRegistrySystem>();
                var protection = world.GetOrCreateSystemManaged<NoBreakZoneProtectionSystem>();

                // The registry asks PugMod's API for the pylon's id, and there is no mod runtime in
                // a batch-mode editor. Reaching in from here beats adding a test seam to shipping
                // code for something only this file will ever use; if the field is renamed, this
                // fails loudly rather than quietly testing nothing.
                if (!TrySetPylonId(registry, (ObjectID)PylonId))
                {
                    report.Check(false, "the registry still has a _pylonObjectID field to seed");
                    return;
                }

                EntityManager em = world.EntityManager;

                Entity pylon = MakeObject(em, PylonId, 0, 0, variation: 1);
                Entity chestInside = MakeObject(em, ChestId, 2, 0);
                Entity chestOutside = MakeObject(em, ChestId, 30, 0);
                Entity floorInside = MakeTile(em, FloorId, 3, 0, TileType.floor);
                Entity oreInside = MakeTile(em, OreId, 4, 0, TileType.ore);
                Entity wallInside = MakeTile(em, WallId, 5, 0, TileType.wall);

                registry.Update();
                protection.Update();

                report.Check(em.HasComponent<NoBreakZonePylonCD>(pylon),
                    "the registry recognised the pylon");

                report.Check(IsIndestructible(em, chestInside),
                    "a chest inside the square is indestructible");
                report.Check(em.HasComponent<NoBreakZoneProtectedCD>(chestInside),
                    "and is claimed as ours, so it can be released again");
                report.Check(BlocksEveryDamageSource(em, chestInside),
                    "and carries the backstop for damage the predicted path never sees");

                report.Check(!IsIndestructible(em, chestOutside),
                    "a chest outside every square is left alone");
                report.Check(em.HasComponent<NoBreakZoneEvaluatedCD>(chestOutside),
                    "but is marked judged, so it is not re-examined every frame");

                // The case the explosion bug is about. A tile is not reached through
                // IndestructibleCD — TileDamageSystem never reads it — so the only thing standing
                // between a floor and destruction is DontDestroyOnZeroHealthCD.
                report.Check(BlocksEveryDamageSource(em, floorInside),
                    "a floor inside the square refuses to die at zero health");
                report.Check(!IsIndestructible(em, floorInside),
                    "and is not given IndestructibleCD, which would be dead weight on every tile");

                // 기획서 §6's one absolute rule. Protecting ore makes a drill mine it forever.
                // design.md:320 — "켜져 있는 동안 파일런은 무적이다. 폭발로도, 곡괭이로도, 몹
                // 공격으로도 파괴되지 않는다." Two components, because they guard different halves
                // of that sentence: IndestructibleCD is what the player's own mining consults, and
                // everything arriving through HealthChangeBuffer reads the other one instead
                // (research.md 8·9장). Until 2026-09-09 only the first was applied, so a switched-on
                // pylon shrugged off a pickaxe and died to the first bomb.
                //
                // That fix is currently held by a single in-game verdict, which costs a play session
                // to reproduce. This is the check that makes a regression cost nothing.
                report.Check(IsIndestructible(em, pylon),
                    "a switched-on pylon refuses the player's own mining");
                report.Check(BlocksEveryDamageSource(em, pylon),
                    "and refuses explosions and mobs too — the other half of design.md:320");

                report.Check(BlocksEveryDamageSource(em, wallInside),
                    "a wall inside the square refuses to die at zero health");

                CheckJudgementDoesNotSetInStone(em, registry, protection, wallInside, report);

                report.Check(!BlocksEveryDamageSource(em, oreInside),
                    "an ore tile inside the square stays breakable");
                report.Check(!em.HasComponent<NoBreakZoneProtectedCD>(oreInside),
                    "and is never claimed as ours");

                // Switching the pylon off has to hand everything back — 기획서 §6's "기지를
                // 수정하려면 파일런을 끄면 된다". Two updates: one to notice, one to release.
                em.SetComponentData(pylon, new ObjectDataCD
                {
                    objectID = (ObjectID)PylonId,
                    amount = 1,
                    variation = 0,
                });

                registry.Update();
                protection.Update();
                registry.Update();
                protection.Update();

                report.Check(!IsIndestructible(em, chestInside),
                    "switching the pylon off releases the chest");
                report.Check(!em.HasComponent<NoBreakZoneProtectedCD>(chestInside),
                    "and drops our claim on it");
                report.Check(!BlocksEveryDamageSource(em, floorInside),
                    "and releases the floor too");

                // 기획서 §6's other half — "회수하려면 먼저 꺼야 한다". A pylon that cannot be
                // picked up again is a trap, and the invulnerability fix above added a second
                // component that could have created exactly that. Nobody has picked one up in game
                // since, so this is the only thing watching the recovery path at all.
                report.Check(!IsIndestructible(em, pylon),
                    "switching a pylon off makes it recoverable again");
                report.Check(!BlocksEveryDamageSource(em, pylon),
                    "and lets go of the second guard as well, or it could never be destroyed");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Check(false, "the protection harness ran without throwing");
            }
            finally
            {
                world.Dispose();
                PugDatabase.objectsByType = savedDatabase;
            }
        }

        /// Chases one of the two live hypotheses behind the resource-duplication failure on main
        /// (`ore-inside-still-breaks`, status.md): does a tile keep the answer it was first given
        /// after the tile itself changes?
        ///
        /// NoBreakZoneProtectionSystem tags every entity it has judged with NoBreakZoneEvaluatedCD
        /// and excludes tagged entities from its query, so an answer is computed once. The tag is
        /// dropped in bulk only when the set of active pylons changes or the diameter does. If a
        /// square judged as WALL — protected since design.md's 2026-08-07 decision — later reads as
        /// ORE, and nothing re-judges it, the ore stays protected. A protected ore tile is an
        /// infinite resource, which 기획서 §6 forbids above every other property of this mod.
        ///
        /// NOTHING TOUCHES THE PYLON HERE, AND THAT IS THE WHOLE POINT. A pylon switching is exactly
        /// the event that clears the tag, so a version of this check that toggled one would re-judge
        /// the tile and pass for a reason that has nothing to do with the question. The first draft
        /// of this check did that and passed. In game there is no pylon change between the wall
        /// being judged and the ore being mined, so neither is there one here.
        ///
        /// A pass does NOT clear the hypothesis. This is a world we built ourselves; it cannot
        /// reproduce how the game creates and recycles tile entities, and the in-game failure may
        /// still come from the other candidate — ore embedded in a wall, so the entity taking the
        /// damage was never the ore. A failure here, on the other hand, would be the answer outright.
        private static void CheckJudgementDoesNotSetInStone(
            EntityManager em,
            NoBreakZonePylonRegistrySystem registry,
            NoBreakZoneProtectionSystem protection,
            Entity wall,
            VerifyReport report)
        {
            em.SetComponentData(wall, new TileCD { tileset = 0, tileType = TileType.ore });

            registry.Update();
            protection.Update();
            registry.Update();
            protection.Update();

            report.Check(!BlocksEveryDamageSource(em, wall),
                "a tile that turns into ore stops being protected, with no pylon change to prompt "
                + "a re-judgement — a stale answer here would duplicate resources (기획서 §6)");

            // Put it back so the release checks below still have a protected wall to release.
            em.SetComponentData(wall, new TileCD { tileset = 0, tileType = TileType.wall });
            registry.Update();
            protection.Update();
        }

        /// PugDatabase.GetObjectInfo reads a plain static dictionary, so the harness can answer for
        /// its own objects. Without this the protection system's size lookup dereferences null.
        private static void InstallFakeDatabase()
        {
            var database = new Dictionary<ObjectDataCD, ObjectInfo>();

            foreach (int id in new[] { PylonId, ChestId, FloorId, OreId, WallId })
            {
                for (int variation = 0; variation <= 1; variation++)
                {
                    database[new ObjectDataCD
                    {
                        objectID = (ObjectID)id,
                        amount = 1,
                        variation = variation,
                    }] = new ObjectInfo
                    {
                        objectID = (ObjectID)id,
                        objectType = ObjectType.PlaceablePrefab,
                        prefabTileSize = Vector2Int.one,
                        prefabCornerOffset = Vector2Int.zero,
                    };
                }
            }

            PugDatabase.objectsByType = database;
        }

        private static bool TrySetPylonId(NoBreakZonePylonRegistrySystem registry, ObjectID id)
        {
            FieldInfo field = typeof(NoBreakZonePylonRegistrySystem)
                .GetField("_pylonObjectID", BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null)
            {
                return false;
            }

            field.SetValue(registry, id);
            return true;
        }

        private static Entity MakeObject(EntityManager em, int id, int x, int z, int variation = 0)
        {
            Entity entity = em.CreateEntity();
            em.AddComponentData(entity, new ObjectDataCD
            {
                objectID = (ObjectID)id,
                amount = 1,
                variation = variation,
            });
            em.AddComponentData(entity, LocalTransform.FromPosition(new float3(x, 0f, z)));
            em.AddComponentData(entity, new HealthCD { health = 1, maxHealth = 1 });
            return entity;
        }

        private static Entity MakeTile(EntityManager em, int id, int x, int z, TileType tileType)
        {
            Entity entity = MakeObject(em, id, x, z);
            em.AddComponentData(entity, new TileCD { tileset = 0, tileType = tileType });
            return entity;
        }

        private static bool IsIndestructible(EntityManager em, Entity entity)
        {
            return em.HasComponent<IndestructibleCD>(entity)
                   && em.IsComponentEnabled<IndestructibleCD>(entity);
        }

        private static bool BlocksEveryDamageSource(EntityManager em, Entity entity)
        {
            return em.HasComponent<DontDestroyOnZeroHealthCD>(entity)
                   && !em.GetComponentData<DontDestroyOnZeroHealthCD>(entity).disabled;
        }
    }
}
