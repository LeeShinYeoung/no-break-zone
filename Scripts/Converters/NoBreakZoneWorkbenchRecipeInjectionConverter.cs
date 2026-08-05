using Pug.Conversion;
using PugMod;
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

    protected override void Convert(CraftingAuthoring authoring)
    {
        if ((ObjectID)ObjectIndex != TargetWorkbench)
        {
            return;
        }

        // Our own objects have no id until the game has registered them. None here means the mod's
        // objects are not in the database yet, and adding a recipe for id 0 would put a broken slot
        // in a vanilla workbench.
        ObjectID workbench = API.Authoring.GetObjectID(NoBreakZoneObjectNames.Workbench);
        if (workbench == ObjectID.None)
        {
            return;
        }

        EnsureHasBuffer<CanCraftObjectsBuffer>();
        AddToBuffer<CanCraftObjectsBuffer>(new CanCraftObjectsBuffer
        {
            objectID = workbench,
            amount = 1,
        });
    }
}
