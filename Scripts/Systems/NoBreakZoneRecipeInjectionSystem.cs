using System;
using System.Text;
using PugMod;
using Unity.Entities;
using UnityEngine;

// Safety net and diagnostics for the Pylon Workbench recipe, after the database exists.
//
// The converter beside this (NoBreakZoneWorkbenchRecipeInjectionConverter) is what normally lands
// the recipe: it writes our object's NAME into the authoring list and the game's own bake resolves
// it. That works no matter what order conversion visits things in, which the converter alone could
// not manage while it tried to resolve a numeric id — conversion reaches the vanilla bench before
// the mod's objects exist, so API.Authoring.GetObjectID answered None and the recipe was dropped in
// silence.
//
// This system runs once the database is up, by which point both halves exist. It appends the recipe
// if it somehow is not there, and — more usefully now — it reports what the target bench actually
// looks like, so a recipe that fails to appear is diagnosed from Player.log instead of guessed at.
//
// WITHOUT A WORKING RECIPE THE MOD IS UNREACHABLE. 기획서 §4 has the pylon crafted at our own
// workbench and the workbench itself at a vanilla bench, and only the game owns that bench.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneRecipeInjectionSystem : PugSimulationSystemBase
{
    // Must match the converter's target. research.md 18장 records why this is the Automation Table
    // and not the iron workbench 기획서 §4 first named: the iron bench authors all 18 of the slots
    // the UI can show, and it absorbs three lower benches, which splits its recipe list into ranges
    // that are drawn one at a time. The Automation Table holds 6 of 18 and absorbs nobody.
    /// Public so NoBreakZoneSelfTestSystem checks the bench this system actually targets rather
    /// than a second copy of the decision that could drift away from it.
    public const ObjectID TargetWorkbench = ObjectID.AutomationTable;

    private bool _done;

    /// True once TryInject has run against a real bench, which is the earliest moment any recipe
    /// is guaranteed to be in place.
    ///
    /// Exists for NoBreakZoneSelfTestSystem. Both systems sit in SimulationSystemGroup with no
    /// ordering between them, so a test reading the recipe buffers on its own schedule could judge
    /// them before this system had written anything and report a failure that says nothing about
    /// the mod. Waiting on a flag beats waiting a guessed number of frames — guessing frame order
    /// is the mistake that hid the explosion bug (Editor/Docs/status.md).
    public bool Done => _done;

    protected override void OnCreate()
    {
        base.OnCreate();
        NeedDatabase();
    }

    protected override void OnUpdate()
    {
        if (!_done && database.IsCreated)
        {
            try
            {
                _done = TryInject();
            }
            catch (Exception e)
            {
                Debug.LogError($"[NoBreakZone] recipe injection failed: {e}");
                _done = true;
            }
        }

        base.OnUpdate();
    }

    /// Returns false to be retried next frame, which is what happens while the mod's own objects
    /// are still being registered.
    private bool TryInject()
    {
        ObjectID workbench = API.Authoring.GetObjectID(NoBreakZoneObjectNames.Workbench);
        if (workbench == ObjectID.None)
        {
            return false;
        }

        ref var infos = ref database.Value.objectInfos;
        var em = EntityManager;
        var injected = 0;
        var alreadyThere = 0;
        var targets = 0;

        for (int i = 0; i < infos.Length; i++)
        {
            ref var info = ref infos[i];
            if (info.objectID != TargetWorkbench)
            {
                continue;
            }

            for (int p = 0; p < info.prefabEntities.Length; p++)
            {
                var entity = info.prefabEntities[p];
                if (!em.Exists(entity))
                {
                    continue;
                }

                targets++;

                if (!em.HasBuffer<CanCraftObjectsBuffer>(entity))
                {
                    em.AddBuffer<CanCraftObjectsBuffer>(entity);
                }

                var recipes = em.GetBuffer<CanCraftObjectsBuffer>(entity);

                // The converter may already have landed this by name. Adding it twice would show
                // the player the same recipe in two slots.
                var ours = -1;
                for (int r = 0; r < recipes.Length; r++)
                {
                    if (recipes[r].objectID == workbench)
                    {
                        ours = r;
                        break;
                    }
                }

                if (ours >= 0)
                {
                    alreadyThere++;
                }
                else
                {
                    ours = recipes.Length;
                    recipes.Add(new CanCraftObjectsBuffer
                    {
                        objectID = workbench,
                        amount = 1,
                    });
                    injected++;
                }

                Report(em, entity, recipes, p, ours);
            }
        }

        Debug.Log($"[NoBreakZone] recipe pass (world={World.Name}): {TargetWorkbench} prefabs={targets}, "
                  + $"injected={injected}, already present={alreadyThere}, "
                  + $"{NoBreakZoneObjectNames.Workbench}={(int)workbench}");

        return targets > 0;
    }

    /// Writes down everything that decides whether the recipe is drawn, so a failure is read off
    /// Player.log rather than guessed at. Three things settle it (research.md 18장):
    ///
    ///   slots       — the UI shows three windows of six, so 18 is the ceiling for one bench.
    ///   ours        — where our recipe sits. Past the end of a range means it is never drawn.
    ///   categories  — a bench that absorbs other benches splits its list into ranges and draws one
    ///                 at a time (the up/down arrows beside the panel). Zero means the whole list is
    ///                 drawn, which is the case appending relies on.
    ///
    /// Empty slots are listed because they are the other way in, and because their presence says the
    /// bench is progression-filtered — AvailableRecipesFromContentBundlesSystem blanks unmet recipes
    /// every 0.2s over the authored range, and would overwrite anything written into one.
    private static void Report(
        EntityManager em,
        Entity prefab,
        DynamicBuffer<CanCraftObjectsBuffer> recipes,
        int prefabIndex,
        int ours)
    {
        var categories = em.HasBuffer<IncludedCraftingBuildingsBuffer>(prefab)
            ? em.GetBuffer<IncludedCraftingBuildingsBuffer>(prefab).Length
            : 0;

        var empties = new StringBuilder();
        for (int r = 0; r < recipes.Length; r++)
        {
            if (recipes[r].objectID != ObjectID.None)
            {
                continue;
            }

            if (empties.Length > 0)
            {
                empties.Append(' ');
            }

            empties.Append(r);
        }

        Debug.Log($"[NoBreakZone] {TargetWorkbench} prefab {prefabIndex}: slots={recipes.Length}, "
                  + $"ours={ours}, categories={categories}, "
                  + $"empty=[{(empties.Length > 0 ? empties.ToString() : "none")}]");
    }
}
