using UnityEngine.Scripting;

// Root component of Prefabs/NoBreakZoneWorkbenchGraphics.prefab. It adds nothing: everything the
// workbench needs — opening the crafting window on E, closing it when the player walks off — is
// already public on CraftingBuilding (ck-db Pug.Other/CraftingBuilding.cs:166,173), and the prefab
// wires those two methods straight into InteractableObject's UnityEvents.
//
// SO WHY DOES THIS EXIST? To pin down how the prefab names its root component. A prefab refers to a
// game class in one of two shapes:
//
//     {fileID: 11500000,        guid: <that .cs file's own meta guid>}   loose script
//     {fileID: <name hash>,     guid: <the assembly's guid>}             compiled into a dll
//
// Both shapes turn up for classes in Pug.Other — EntityMonoBehaviour is the first, InteractableObject
// the second — and no reference prefab anywhere uses stock CraftingBuilding, so there is nothing to
// copy and no way to tell which shape it wants without a Unity editor. Deriving our own class makes
// the question moot: this file is ours, so it is the first shape by construction and its guid is one
// Editor/genassets.py computes. The SDK's own workbench example subclasses CraftingBuilding too.
//
// When 5단계 adds effects, this is where they go — CraftingBuilding.Use() is virtual and OnDeath()
// is the SDK example's hook for a dust puff.
//
// [Preserve] keeps the linker from stripping a class nothing references from C#; the prefab is the
// only thing that names it.
[Preserve]
public class NoBreakZoneWorkbenchGraphics : CraftingBuilding
{
}
