using System;
using PugMod;
using Unity.Entities;
using UnityEngine;

// Puts the Pylon Workbench recipe on the vanilla iron workbench, after the database exists.
//
// The converter cannot finish this job. Conversion visits the vanilla workbench before it visits
// the mod's own objects -- Player.log shows IronWorkBench (4010) converted, and our workbench
// arriving as 32770 only afterwards -- so at the moment the converter runs there is no number to
// point at. API.Authoring.GetObjectID answers None and the recipe used to be dropped in silence.
//
// The recipe list is a buffer on the workbench's prefab entity, and that entity is still there once
// the database is up, by which time the mod's objects do have ids. So the entry is appended here
// instead, where both halves exist at the same time.
//
// WITHOUT A WORKING RECIPE THE MOD IS UNREACHABLE. 기획서 §4 has the pylon crafted at our own
// workbench and the workbench itself at an iron-tier bench, and only the game owns that bench.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneRecipeInjectionSystem : PugSimulationSystemBase
{
    // 기획서 §4: "철 계열 작업대". The real gate on the pylon is ancient gemstones and mechanical
    // parts from the Forgotten Ruins, so hanging it off a lower bench would show players a recipe
    // they cannot fill for a long stretch.
    private const ObjectID TargetWorkbench = ObjectID.IronWorkBench;

    private bool _done;

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
                var present = false;
                for (int r = 0; r < recipes.Length; r++)
                {
                    if (recipes[r].objectID == workbench)
                    {
                        present = true;
                        break;
                    }
                }

                if (present)
                {
                    alreadyThere++;
                }
                else
                {
                    // Says how long the vanilla list was, which separates "our entry never arrived"
                    // from "it arrived and the interface did not show it".
                    Debug.Log($"[NoBreakZone] {TargetWorkbench} prefab {p} has {recipes.Length} recipe(s) "
                              + $"before ours (world={World.Name})");

                    recipes.Add(new CanCraftObjectsBuffer
                    {
                        objectID = workbench,
                        amount = 1,
                    });
                    injected++;
                }

                // Runs whichever way the recipe got here. The converter lands it by name before this
                // system ever sees the workbench, so gating the category on our own insert meant it
                // was never added at all.
                AddCategoryFor(entity, workbench);
            }
        }

        Debug.Log($"[NoBreakZone] recipe pass (world={World.Name}): {TargetWorkbench} prefabs={targets}, "
                  + $"injected={injected}, already present={alreadyThere}, "
                  + $"{NoBreakZoneObjectNames.Workbench}={(int)workbench}");

        return targets > 0;
    }

    /// Gives our recipe a category of its own on the target workbench.
    ///
    /// Appending to CanCraftObjectsBuffer alone is not enough, and the reason is in
    /// SimpleCraftingUIContainer.ShowCraftingUI: it walks the *current category's* slot range in
    /// steps of six and opens a window for every chunk holding an available recipe. It owns three
    /// windows. The iron workbench's 72 recipes already spread across three chunks, so a recipe
    /// appended at slot 72 asked for a fourth and the game logged
    /// "Not enough SimpleCraftingUIs ... Needed at least 4, but only have 3" and drew nothing.
    ///
    /// Categories are not authored in the UI: CraftingBuilding.OnOccupied builds them from
    /// IncludedCraftingBuildingsBuffer, one category per element, deriving startSlotIndex and
    /// endSlotIndex by accumulating amountOfCraftingOptions in buffer order. Appending an element
    /// there gives our single recipe its own category, whose range is one slot wide and therefore
    /// one chunk and one window.
    ///
    /// Both appends have to stay in step: the categories' amounts are summed to locate each range,
    /// so a category added without its recipe (or the other way round) would point at somebody
    /// else's slot.
    private void AddCategoryFor(Entity workbenchPrefab, ObjectID ours)
    {
        var em = EntityManager;

        if (!em.HasBuffer<IncludedCraftingBuildingsBuffer>(workbenchPrefab))
        {
            // No categories at all means the whole recipe list is shown as one range, which is the
            // case this fix does not apply to.
            Debug.Log($"[NoBreakZone] {TargetWorkbench} has no crafting categories — nothing to add to");
            return;
        }

        var categories = em.GetBuffer<IncludedCraftingBuildingsBuffer>(workbenchPrefab);

        var covered = 0;
        for (int c = 0; c < categories.Length; c++)
        {
            if (categories[c].objectID == ours)
            {
                return;
            }

            covered += categories[c].amountOfCraftingOptions;
        }

        categories.Add(new IncludedCraftingBuildingsBuffer
        {
            objectID = ours,
            amountOfCraftingOptions = 1,
        });

        // If the existing categories do not add up to where our recipe actually sits, the new
        // category points at the wrong slot and would show somebody else's recipe.
        Debug.Log($"[NoBreakZone] added crafting category on {TargetWorkbench}: "
                  + $"{categories.Length} categories, earlier ones cover {covered} slot(s)");
    }
}
