using System;
using PugMod;
using UnityEngine;

// Files the mod's four items into the creative mode item window (design.md §4, 크리에이티브 모드).
//
// WHY THE WINDOW MISSES THEM ON ITS OWN. It lists only objects whose id sits in one of
// ObjectIDCategoryManager.SubCategories — CraftingSelectorDataAllObjects.Start builds the list from
// nothing else. ModManager.Init does try to file every mod object under a "Mods" category, but it
// runs before ECSManager.Init hands out mod object ids, so what it files is ObjectID.None, and that
// category is not an asset the game ships. A mod item nobody files is simply never in the window.
//
// WHY VANILLA CATEGORIES AND NOT OUR OWN. The window's tabs are a list baked into its prefab
// (CraftingSelectorFilterCategoryUI.categories), so a category of ours would get no tab. And one made
// with ScriptableObject.CreateInstance has a null id list — nothing deserialized it — so its Add
// throws. Filing each item beside its vanilla neighbours needs neither.
//
// WHEN. The window builds its list once per process, the first time an inventory opens in a
// creative world, and never rebuilds it. IMod.Init runs after every manager — ECSManager included —
// has finished, so the ids exist by then; Update keeps retrying anyway while any id still reads
// None, the same guard NoBreakZoneRecipeInjectionSystem uses.
//
// NOTHING HERE IS SAVED. It edits the in-memory id sets of three game categories, which the next
// launch loads fresh from the game's assets, and the creative window is their only reader in the
// game. The server hands out a creative item without looking at categories, so this changes what a
// player can find, never what a world contains.
public static class NoBreakZoneCreativeMenu
{
    private struct Placement
    {
        public readonly string ObjectName;
        public readonly string Category;  // asset name of a vanilla subcategory, "Parent_Child"

        public Placement(string objectName, string category)
        {
            ObjectName = objectName;
            Category = category;
        }
    }

    // Where a player would already look: the pylon with the levers and sensors, the workbench with
    // the other benches (the Automation Table that makes it among them), the two handheld tools with
    // the other tools.
    private static readonly Placement[] Placements =
    {
        new Placement(NoBreakZoneObjectNames.Pylon, "Technology_Electronics"),
        new Placement(NoBreakZoneObjectNames.Workbench, "Building_CraftingStation"),
        new Placement(NoBreakZoneObjectNames.Lens, "Tool_Other"),
        new Placement(NoBreakZoneObjectNames.Remote, "Tool_Other"),
    };

    private static bool _done;

    /// Cheap to call every frame: it does the work once, the first time all four ids resolve.
    public static void TryRegister()
    {
        if (_done)
        {
            return;
        }

        try
        {
            _done = Register();
        }
        catch (Exception e)
        {
            // Never take the mod down over where its items are listed.
            Debug.LogError($"[NoBreakZone] creative menu registration failed: {e}");
            _done = true;
        }
    }

    /// Returns false to be retried next frame.
    private static bool Register()
    {
        // Every id before any filing, so a retry never meets a half-filed set.
        var ids = new ObjectID[Placements.Length];
        for (int i = 0; i < Placements.Length; i++)
        {
            ids[i] = API.Authoring.GetObjectID(Placements[i].ObjectName);
            if (ids[i] == ObjectID.None)
            {
                return false;
            }
        }

        var filed = 0;
        for (int i = 0; i < Placements.Length; i++)
        {
            var category = FindSubCategory(Placements[i].Category);
            if (category == null)
            {
                // A game update renamed or dropped it. The item is still craftable as before.
                Debug.LogWarning($"[NoBreakZone] creative menu: no category {Placements[i].Category}, so {Placements[i].ObjectName} is left out of it");
                continue;
            }

            if (!category.Contains(ids[i]))
            {
                category.Add(ids[i]);

                // ObjectIDCategory.Add only appends to the serialized list. The set the window
                // actually reads (ObjectIds, and the parent tab's copy of it) is rebuilt by
                // SetParentCategory — handing it the parent it already has does exactly that and
                // nothing else. Without this line the call succeeds and nothing appears.
                category.SetParentCategory(category.ParentCategory);
            }

            filed++;
        }

        Debug.Log($"[NoBreakZone] creative menu: {filed} of {Placements.Length} items filed");
        return true;
    }

    private static ObjectIDCategory FindSubCategory(string assetName)
    {
        foreach (var category in ObjectIDCategoryManager.SubCategories)
        {
            if (category != null && category.name == assetName)
            {
                return category;
            }
        }

        return null;
    }
}
