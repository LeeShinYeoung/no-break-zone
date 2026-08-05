using System.Collections.Generic;
using Pug.Conversion;
using PugMod;
using UnityEngine;
using UnityEngine.Scripting;

// Makes the Pylon Workbench craftable at a vanilla iron workbench.
//
// A mod cannot edit the game's own prefabs, so a recipe is added by converting them as they bake:
// this runs once per authoring object that carries CraftingAuthoring, and appends to the recipe
// buffer of the one we want. The pattern is ConveyorTunnelMod's
// (ck-mods/.../ConveyorTunnelRecipeInjectionConverter.cs), which does the same to the Automation
// Table.
//
// WITHOUT THIS THE MOD IS UNREACHABLE. 기획서 §4 has the pylon crafted at our own workbench, and the
// workbench itself at an iron-tier bench — but only the game owns that bench. Nothing else in the
// mod can put a recipe there, so this is the single door into everything the mod adds.
//
// 기획서 §4 chose iron tier deliberately: the real gate on the pylon is ancient gemstones and
// mechanical parts from the Forgotten Ruins, so hanging the workbench off a lower bench would show
// players a recipe they cannot fill for a long stretch.
[Preserve]
public class NoBreakZoneWorkbenchRecipeInjectionConverter
    : SingleAuthoringComponentConverter<CraftingAuthoring>
{
    // 기획서 §4: "철 계열 작업대". Named rather than cast from a number — the number is resolved
    // against the real game assembly at build time, so it cannot drift the way a literal can.
    // (ConveyorTunnelMod hardcodes (ObjectID)4022; that value still matches the current game, but
    // only because nothing has shifted it yet.)
    private const ObjectID TargetWorkbench = ObjectID.IronWorkBench;

    // Both ways this can fail used to be a bare return, so a missing recipe looked exactly like a
    // converter that never ran. Every station is named once instead: if TargetWorkbench is absent
    // from that list the match is what is wrong, and if it is present the line after it says
    // whether our own object had an id yet.
    private static readonly HashSet<int> Reported = new HashSet<int>();

    protected override void Convert(CraftingAuthoring authoring)
    {
        if (Reported.Add(ObjectIndex))
        {
            Debug.Log($"[NoBreakZone] crafting station seen: {(ObjectID)ObjectIndex} ({ObjectIndex})");
        }

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
