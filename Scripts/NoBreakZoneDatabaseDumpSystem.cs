using System;
using System.Collections.Generic;
using System.Text;
using Unity.Entities;
using UnityEngine;

// DEV AUDIT TOOL (stage-4 support).
// One-shot dump of the ENTIRE object database into a static, project-committed reference of every
// object's ObjectType + gameplay-relevant component flags, so we design the protection rules from data
// instead of guessing per object and rebuilding/relaunching each time.
//
// Component NAMES cannot be read at runtime in a release build (ComponentType.ToString relies on
// DebugTypeName, which is stripped, and reflection is banned in mod code). Instead we probe a curated
// set of known components with the generic HasComponent<T> (reflection-free) and log a flag table.
// Runs once after the database is ready, writes CSV-style [NBZDB] lines to Player.log, then stops.
// Development tool — gate or remove before shipping.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneDatabaseDumpSystem : PugSimulationSystemBase
{
    // Set true and rebuild to regenerate Editor/GameData/object_flags.csv (e.g. after a
    // game update). Left OFF so normal test builds don't spam ~2300 lines into Player.log.
    private const bool RunAudit = false;

    private bool _done;

    protected override void OnCreate()
    {
        base.OnCreate();
        NeedDatabase();
    }

    protected override void OnUpdate()
    {
        if (RunAudit && !_done && database.IsCreated)
        {
            try
            {
                DumpAll();
            }
            catch (Exception e)
            {
                Debug.LogError($"[NBZDB] dump failed: {e}");
            }
            _done = true;
        }

        base.OnUpdate();
    }

    private void DumpAll()
    {
        ref var infos = ref database.Value.objectInfos;
        var seen = new HashSet<ObjectID>();
        var sb = new StringBuilder(256);
        var em = EntityManager;
        int unique = 0;

        Debug.Log("[NBZDB] COLUMNS=id,type,tileType,health,damageable,destructible,dontDestroy,immune,lootTable,lootOnDmg,dontDropSelf,dontDropLoot,mineable,diggable,requiresDrill,tileCD,plant,growing,critter,catTags");
        Debug.Log($"[NBZDB] BEGIN objectInfos={infos.Length}");

        for (int i = 0; i < infos.Length; i++)
        {
            ref var info = ref infos[i];
            var id = info.objectID;
            if (!seen.Add(id))
            {
                continue;
            }

            sb.Clear();
            sb.Append("[NBZDB] ").Append(id).Append(',').Append(info.objectType).Append(',').Append(info.tileType);

            if (info.prefabEntities.Length > 0 && em.Exists(info.prefabEntities[0]))
            {
                var e = info.prefabEntities[0];
                Flag(sb, em.HasComponent<HealthCD>(e));
                Flag(sb, em.HasComponent<DamageableObjectCD>(e));
                Flag(sb, em.HasComponent<DestructibleObjectCD>(e));
                Flag(sb, em.HasComponent<DontDestroyOnZeroHealthCD>(e));
                Flag(sb, em.HasComponent<ImmuneToDamageCD>(e));
                Flag(sb, em.HasComponent<DropsLootFromLootTableCD>(e));
                Flag(sb, em.HasComponent<DropsLootWhenDamagedCD>(e));
                Flag(sb, em.HasComponent<DontDropSelfCD>(e));
                Flag(sb, em.HasComponent<DontDropLootCD>(e));
                Flag(sb, em.HasComponent<MineableCD>(e));
                Flag(sb, em.HasComponent<DiggableCD>(e));
                Flag(sb, em.HasComponent<RequiresDrillCD>(e));
                Flag(sb, em.HasComponent<TileCD>(e));
                Flag(sb, em.HasComponent<PlantCD>(e));
                Flag(sb, em.HasComponent<GrowingCD>(e));
                Flag(sb, em.HasComponent<CritterCD>(e));
                Flag(sb, em.HasComponent<ObjectCategoryTagsCD>(e));
            }
            else
            {
                sb.Append(",<no-prefab>");
            }

            Debug.Log(sb.ToString());
            unique++;
        }

        Debug.Log($"[NBZDB] END unique={unique}");
    }

    private static void Flag(StringBuilder sb, bool value)
    {
        sb.Append(',').Append(value ? '1' : '0');
    }
}
