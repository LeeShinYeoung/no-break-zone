using Pug.Conversion;
using PugMod;
using UnityEngine;
using UnityEngine.Scripting;

// Makes the Pylon Workbench craftable at the vanilla Automation Table.
//
// A mod cannot edit the game's own prefabs, so a recipe is added by converting them as they bake:
// this runs once per authoring object that carries CraftingAuthoring, and appends to the recipe
// buffer of the one we want. The pattern is ConveyorTunnelMod's
// (ck-mods/.../ConveyorTunnelRecipeInjectionConverter.cs), which appends to this very bench.
//
// WITHOUT THIS THE MOD IS UNREACHABLE. 기획서 §4 has the pylon crafted at our own workbench, and the
// workbench itself at a vanilla bench that only the game owns. Nothing else in the mod can put a
// recipe there, so this is the single door into everything the mod adds.
//
// WHY NOT THE IRON WORKBENCH, WHICH 기획서 §4 ORIGINALLY NAMED: it is full. A bench shows three
// windows of six, so 18 recipes is the ceiling, and the iron workbench authors exactly 18. Worse,
// it absorbs the basic, copper and tin benches, and an absorbed bench splits the recipe list into
// ranges that the UI draws one at a time (research.md 18장) — so a recipe appended past the end
// falls outside every range and is never drawn. That is what the player was seeing.
//
// The Automation Table holds 6 of its 18 and absorbs nobody, so appending is enough. Two shipped
// mods do exactly this (ConveyorTunnelMod, limoka's DummyMod). Its subject matter fits too: the
// pylon is a machine and takes mechanical parts.
[Preserve]
public class NoBreakZoneWorkbenchRecipeInjectionConverter
    : SingleAuthoringComponentConverter<CraftingAuthoring>
{
    // Named rather than cast from a number — the number is resolved against the real game assembly
    // at build time, so it cannot drift the way a literal can. (ConveyorTunnelMod hardcodes
    // (ObjectID)4022; that value still matches the current game, but only because nothing has
    // shifted it yet.)
    private const ObjectID TargetWorkbench = ObjectID.AutomationTable;

    protected override void Convert(CraftingAuthoring authoring)
    {
        if ((ObjectID)ObjectIndex != TargetWorkbench)
        {
            return;
        }

        // The mod's own objects have no numeric id yet: conversion runs before the database is
        // built, so API.Authoring.GetObjectID answers None here and the recipe used to be dropped
        // in silence. research.md 12장 already recorded the answer -- a mod object is named, not
        // numbered, and the game resolves the name during its own bake. This is the same form our
        // own workbench prefab uses to point at the pylon.
        foreach (CraftingAuthoring.CraftableObject existing in authoring.canCraftObjects)
        {
            // Conversion can visit the same authoring object more than once; a second copy would
            // show the player a duplicate recipe.
            if (existing.moddedObjectID == NoBreakZoneObjectNames.Workbench)
            {
                return;
            }
        }

        authoring.canCraftObjects.Add(new CraftingAuthoring.CraftableObject
        {
            objectID = ObjectID.None,
            moddedObjectID = NoBreakZoneObjectNames.Workbench,
            amount = 1,
            craftingTime = 3f,
        });

        Debug.Log($"[NoBreakZone] recipe injected into {TargetWorkbench} by name: "
                  + NoBreakZoneObjectNames.Workbench);
    }
}
