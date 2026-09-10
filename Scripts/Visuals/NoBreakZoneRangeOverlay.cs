using System.Collections.Generic;
using PugMod;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

// 기획서 §7's range display, and the only thing that ever draws one.
//
// "커서를 올려도, 파일런을 손에 들어도, 파일런을 켜고 꺼도 범위는 보이지 않는다. 오직 렌즈뿐이다."
// That exclusivity is deliberate — §7 argues that if the range showed up anywhere else the lens
// would be a decoration nobody needs to craft. So there is one entry point, gated on one item.
//
// CLIENT ONLY. Driven from NoBreakZoneMod.Update (IMod.Update), never from a system, so it cannot
// end up in the server simulation (design.md §9). It reads the pylon registry out of the client
// world, which already runs there — NoBreakZonePylonRegistrySystem is filtered to both worlds.
public static class NoBreakZoneRangeOverlay
{
    // Sprite subasset name of Textures/NoBreakZoneRangeMarker.png. genassets.py writes that file and
    // names the constant beside it; the two have to agree or no marker is ever found.
    public const string MarkerSpriteName = "NoBreakZoneRangeMarker";

    // How far from the player we bother drawing. 기획서 §7 says "화면 안 모든 파일런", but screen
    // size is not a fixed number of tiles: design.md §12 already rejected defining the remote's
    // reach that way because "모니터가 큰 사람이 유리해지고, 멀티플레이에서는 플레이어마다 사거리가
    // 달라진다". Same argument, same answer — a fixed tile radius, generous enough to cover any
    // sane window, which also caps how many markers can exist.
    private const int DrawRadiusTiles = 48;

    private static Sprite _markerSprite;
    private static Transform _root;
    private static readonly List<SpriteRenderer> _markers = new List<SpriteRenderer>();

    private static ObjectID _lensObjectID = ObjectID.None;
    private static bool _warnedNoSprite;

    // DIAGNOSTIC, remove once the lens is confirmed visible in game.
    private static ObjectID _lastLoggedHeld = (ObjectID)(-1);
    private static bool _loggedFirstDraw;
    private static bool _warnedNoIcon;
    private static bool _loggedMarkerSetup;
    private static Color _markerColour = Color.white;

    /// The one thing PlacementIcon sets that a copied material does not carry, and the whole reason
    /// the lens showed nothing for a month.
    ///
    /// PlaceIconAmplify is driven by a shader float the game names "_transparancy". PlacementIcon
    /// writes it every frame on its own material INSTANCE, ramping 0 -> 0.5 while the player stands
    /// still and back to 0 when they move:
    ///
    ///     currentFadeValue = Mathf.Clamp(currentFadeValue + Time.deltaTime * num * 2f, 0f, 0.5f);
    ///     ((Renderer)SR).material.SetFloat(Transparancy, currentFadeValue);
    ///
    /// So 0 is invisible and 0.5 is as shown as the game ever shows it. We copied the SHARED material,
    /// whose stored value is the invisible default, and never set the float — rotation, colour,
    /// height, sorting and layer all matched a sprite that renders, and the markers were there the
    /// whole time at zero.
    private static readonly int TransparancyProperty = Shader.PropertyToID("_transparancy");
    private const float VisibleTransparancy = 0.5f;
    private static MaterialPropertyBlock _propertyBlock;

    // One tile is 16 texture pixels — SpriteObject.PixelsPerUnit is a hardcoded 16f and the marker
    // texture is one tile wide, so the sprite has to be built at the same scale or every edge comes
    // out the wrong length. genassets.py draws the texture at MARKER_TILE_PIXELS for the same reason.
    private const float MarkerPixelsPerUnit = 16f;

    /// Called from NoBreakZoneMod.ModObjectLoaded for every asset in the bundle. Matching by name is
    /// how the reference mod picks up its own helper sprites — the mod never gets a path or a guid
    /// at runtime, only the loaded objects.
    public static void RegisterLoadedObject(Object obj)
    {
        // A mod never sees a path or a guid, so "the sprite is missing" is indistinguishable from
        // "the sprite arrived under another name" unless the inventory is written down.
        //
        // The kind is spelled out with type tests rather than GetType().Name: that call is
        // System.Reflection.MemberInfo.get_Name, which the game's mod safety check rejects
        // outright, and a rejected assembly means the whole mod fails to load.
        string kind = obj is Sprite ? "Sprite"
            : obj is Texture2D ? "Texture2D"
            : obj is GameObject ? "GameObject"
            : obj is Material ? "Material"
            : "other";
        Debug.Log($"[NoBreakZone] bundle object: {kind} '{obj.name}'");

        if (obj is Sprite sprite && sprite.name == MarkerSpriteName)
        {
            _markerSprite = sprite;
            return;
        }

        // NO SPRITE EVER ARRIVES. Logging the whole inventory showed what the bundle actually hands
        // a mod: Texture2D, GameObject and ScriptableObject, and not one Sprite — so waiting for the
        // Sprite subasset the .meta's spriteMode:1 produces meant waiting forever, and the lens drew
        // nothing while the log said "no 'NoBreakZoneRangeMarker' sprite loaded".
        //
        // The texture does arrive, and a Sprite is only a rect and a pivot over one. Building it
        // here needs no read/write access — Sprite.Create references the texture rather than reading
        // its pixels — and it is the same picture the importer would have made.
        if (_markerSprite == null && obj is Texture2D texture && texture.name == MarkerSpriteName)
        {
            _markerSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                MarkerPixelsPerUnit);
            _markerSprite.name = MarkerSpriteName;
        }
    }

    /// Called every frame from NoBreakZoneMod.Update.
    public static void Update()
    {
        if (!ShouldDraw())
        {
            HideAll();
            return;
        }

        var pylons = GetActivePylons();
        if (!pylons.IsCreated || pylons.Length == 0)
        {
            HideAll();
            return;
        }

        // Read from config so the outline always shows the area actually being protected — the two
        // disagreeing would be worse than no outline at all.
        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter);
        int2 player = PlayerTile();
        int used = 0;

        // The square spans [-radius, +radius] tiles around the pylon, and a tile is a unit wide, so
        // the outline sits half a tile beyond the outermost protected tile on each side.
        float half = radius + 0.5f;
        float length = radius * 2 + 1;

        for (int i = 0; i < pylons.Length; i++)
        {
            int2 pylon = pylons[i];

            if (math.abs(pylon.x - player.x) > DrawRadiusTiles + radius ||
                math.abs(pylon.y - player.y) > DrawRadiusTiles + radius)
            {
                continue;
            }

            // 기획서 §9: "범위 표시를 타일마다 오브젝트를 생성해 구현하지 않는다. 21×21이면 경계만
            // 해도 80칸이다." Four stretched segments draw the same outline the player sees, at a
            // twentieth of the objects.
            //
            // Top and bottom run the full width so they cover the corners; the sides span the same
            // length and overlap them, which is what keeps the corners closed.
            PlaceEdge(used++, pylon.x, pylon.y + half, length, horizontal: true);
            PlaceEdge(used++, pylon.x, pylon.y - half, length, horizontal: true);
            PlaceEdge(used++, pylon.x - half, pylon.y, length, horizontal: false);
            PlaceEdge(used++, pylon.x + half, pylon.y, length, horizontal: false);
        }

        // DIAGNOSTIC, remove once the lens is understood. If this line appears and the player still
        // saw nothing, the markers exist and the problem is how they are drawn —
        // CopyAppearanceFromPlacementIcon is the first suspect. If it never appears, the lens was
        // never detected in hand and the check above is what to fix.
        if (!_loggedFirstDraw && used > 0)
        {
            _loggedFirstDraw = true;
            Debug.Log($"[NoBreakZone] lens drew {used} marker(s), radius={radius}, "
                      + $"sprite={_markerSprite.name} {_markerSprite.rect.width}x"
                      + $"{_markerSprite.rect.height}px ppu={_markerSprite.pixelsPerUnit}");
        }

        // Everything the pool still holds beyond what this frame needed.
        for (int i = used; i < _markers.Count; i++)
        {
            SetActive(_markers[i], false);
        }
    }

    /// Called from NoBreakZoneMod.Shutdown so a reloaded mod does not leave markers behind.
    public static void Dispose()
    {
        for (int i = 0; i < _markers.Count; i++)
        {
            if (_markers[i] != null)
            {
                Object.Destroy(_markers[i].gameObject);
            }
        }

        _markers.Clear();

        if (_root != null)
        {
            Object.Destroy(_root.gameObject);
            _root = null;
        }

        // The one-shot diagnostics fire once per PROCESS otherwise, and on 2026-09-10 that cost an
        // hour: a second world in the same session drew its markers silently, and the absent log
        // line read as "never drawn". Reset them with the markers so each world reports afresh.
        _loggedFirstDraw = false;
        _loggedMarkerSetup = false;
        _warnedNoIcon = false;
        _lastLoggedHeld = ObjectID.None;
    }

    private static bool ShouldDraw()
    {
        // design.md §10. 기획서 §7 already limits the display to the lens; this switches off even
        // that, for players who would rather never see markers on their floor.
        if (!NoBreakZoneConfig.ShowRangeWithLens)
        {
            return false;
        }

        if (_markerSprite == null)
        {
            if (!_warnedNoSprite)
            {
                _warnedNoSprite = true;
                Debug.LogWarning($"[NoBreakZone] no '{MarkerSpriteName}' sprite loaded — the lens "
                                 + "will show nothing");
            }

            return false;
        }

        var manager = Manager.main;
        if (manager == null || manager.player == null)
        {
            return false;
        }

        // The reference mod hides its own guide on both of these. A range drawn under an open map
        // or inventory is just clutter the player cannot act on.
        if (Manager.ui != null && (Manager.ui.isShowingMap || Manager.ui.isAnyInventoryShowing))
        {
            return false;
        }

        return IsHoldingLens(manager.player);
    }

    private static bool IsHoldingLens(PlayerController player)
    {
        if (_lensObjectID == ObjectID.None)
        {
            _lensObjectID = API.Authoring.GetObjectID(NoBreakZoneObjectNames.Lens);
            if (_lensObjectID == ObjectID.None)
            {
                return false;  // object database not up yet
            }
        }

        ObjectID held = player.visuallyEquippedContainedObject.objectData.objectID;

        // DIAGNOSTIC, remove once the lens is understood. Holding the lens changed nothing in game,
        // and there are only two ways that happens: this test never became true, or it did and the
        // markers were drawn invisibly. One line each settles it.
        //
        // A KeyItem may never be "visually equipped" at all — the field is paired with an
        // EquipmentSlotType — so the equipped slot's own contents are printed beside it. If they
        // disagree, the fix is to read the slot instead.
        if (held != _lastLoggedHeld)
        {
            _lastLoggedHeld = held;
            Debug.Log($"[NoBreakZone] lens check: visuallyEquipped={held} lens={_lensObjectID} "
                      + $"equippedSlot={player.equippedSlotIndex}");
        }

        return held == _lensObjectID;
    }

    // 기획서 §7 shows the range of pylons that are ON. The registry already filters to those, so a
    // switched-off pylon showing no outline falls out of 4단계's work rather than needing its own
    // check here.
    private static NativeArray<int2> GetActivePylons()
    {
        var world = API.Client?.World;
        if (world == null || !world.IsCreated)
        {
            return default;
        }

        var registry = world.GetExistingSystemManaged<NoBreakZonePylonRegistrySystem>();
        return registry == null ? default : registry.Positions;
    }

    private static int2 PlayerTile()
    {
        Vector3 position = Manager.main.player.transform.position;
        return new int2(Mathf.RoundToInt(position.x), Mathf.RoundToInt(position.z));
    }

    /// One side of the square, stretched to length.
    ///
    /// Only the length axis is scaled. The segment is uniform along that axis, so stretching cannot
    /// distort it, and leaving the other axis alone keeps the line exactly as thick as it was drawn.
    private static void PlaceEdge(int index, float centreX, float centreZ, float length,
                                  bool horizontal)
    {
        while (_markers.Count <= index)
        {
            _markers.Add(CreateMarker());
        }

        SpriteRenderer marker = _markers[index];
        if (marker == null)
        {
            return;
        }

        // Flat on the ground, like the game's own tile-level sprites: the sprite quad stands upright
        // by default, so it is rotated a quarter turn about X. The extra turn about Z is what makes
        // a side run along Z instead of X — UNVERIFIED, and the first thing to adjust if the sides
        // come out crossed. The small lift avoids z-fighting with the floor.
        marker.transform.SetPositionAndRotation(
            new Vector3(centreX, 0.02f, centreZ),
            Quaternion.Euler(90f, 0f, horizontal ? 0f : 90f));

        marker.transform.localScale = new Vector3(length, 1f, 1f);
        marker.color = _markerColour;

        if (NoBreakZoneConfig.LensDebugMarkers)
        {
            // Everything a marker could be failing on, pushed past any doubt at once: opaque
            // magenta so tinting cannot hide it, thick so a sliver cannot be missed, and lifted a
            // long way clear of the floor so nothing at ground level can cover it. Nobody would
            // ship this; the point is that seeing it narrows the fault to appearance, and not
            // seeing it rules appearance out entirely.
            marker.color = Color.magenta;
            marker.transform.localScale = new Vector3(length, 8f, 1f);
            marker.transform.position = new Vector3(centreX, 1.5f, centreZ);
        }
        SetActive(marker, true);
    }

    private static SpriteRenderer CreateMarker()
    {
        if (_root == null)
        {
            var rootObject = new GameObject("NoBreakZoneRangeOverlay");
            Object.DontDestroyOnLoad(rootObject);
            _root = rootObject.transform;
        }

        var markerObject = new GameObject("RangeMarker");
        markerObject.transform.SetParent(_root, false);

        var renderer = markerObject.AddComponent<SpriteRenderer>();
        renderer.sprite = _markerSprite;
        CopyAppearanceFromPlacementIcon(renderer);

        markerObject.SetActive(false);
        return renderer;
    }

    // UNVERIFIED, AND THE MOST LIKELY THING TO BE WRONG HERE. A bare SpriteRenderer has no material
    // that suits this game's render pipeline, and unlike the reference mod we are not placing an
    // object, so there is no PlacementIcon handed to us to copy from. Borrowing the one in the scene
    // keeps the marker on the game's own sorting layer with the game's own material; if it is not
    // found, Unity's default sprite material is what gets used and the marker may be invisible or
    // drawn through walls.
    private static void CopyAppearanceFromPlacementIcon(SpriteRenderer renderer)
    {
        var icon = Object.FindObjectOfType<PlacementIcon>(true);
        if (icon == null || icon.SR == null)
        {
            // Said out loud rather than returned from silently: a marker left on Unity's default
            // sprite material is one of the two ways "the lens does nothing" can happen, and it used
            // to leave no trace at all.
            if (!_warnedNoIcon)
            {
                _warnedNoIcon = true;
                Debug.LogWarning("[NoBreakZone] no PlacementIcon to copy from — the range markers "
                                 + "keep Unity's default sprite material and may not draw");
            }

            return;
        }

        // COLOUR IS COPIED TOO, AND WAS NOT BEFORE. Everything else here came from the icon while
        // the tint was left at whatever a fresh SpriteRenderer defaults to. On a material that
        // multiplies by vertex colour that alone can render a sprite invisible, which puts it on the
        // short list of reasons the markers exist and cannot be seen.
        _markerColour = icon.SR.color;

        renderer.sharedMaterial = icon.SR.sharedMaterial;
        renderer.sortingLayerID = icon.SR.sortingLayerID;
        // Above the placement icon rather than below it. Below was the tidier choice — an outline is
        // background information — but it also put the marker behind whatever the game draws at
        // ground level, which is one of the two ways it could have gone missing. Order first, taste
        // afterwards.
        renderer.sortingOrder = icon.SR.sortingOrder + 1;
        renderer.maskInteraction = icon.SR.maskInteraction;
        renderer.gameObject.layer = icon.SR.gameObject.layer;

        // A property block rather than renderer.material: the latter clones the material per
        // marker and the clones outlive the GameObjects Dispose destroys, so a mod reload would
        // leak four materials a time. A block overrides the float on this renderer only, leaves
        // the shared asset alone for the game's own icon, and costs nothing to drop.
        _propertyBlock ??= new MaterialPropertyBlock();
        renderer.GetPropertyBlock(_propertyBlock);
        _propertyBlock.SetFloat(TransparancyProperty, VisibleTransparancy);
        renderer.SetPropertyBlock(_propertyBlock);

        if (!_loggedMarkerSetup)
        {
            _loggedMarkerSetup = true;

            // THE REFERENCE HALF IS THE POINT. The lines above copy the icon's material and sorting;
            // rotation, colour and height we still choose ourselves, and those three are all that is
            // left to explain markers that exist and cannot be seen. The icon is a ground sprite
            // that demonstrably renders, so what IT uses is the answer — printed here rather than
            // guessed at, which is the lesson research.md 20장 cost three play sessions to learn.
            Transform iconTransform = icon.SR.transform;
            Debug.Log($"[NoBreakZone] marker material={renderer.sharedMaterial.name} "
                      + $"sortingLayer={renderer.sortingLayerID} order={renderer.sortingOrder} "
                      + $"layer={renderer.gameObject.layer}");
            Debug.Log($"[NoBreakZone] reference icon: rotation={iconTransform.eulerAngles} "
                      + $"colour={icon.SR.color} y={iconTransform.position.y} "
                      + $"enabled={icon.SR.enabled} scale={iconTransform.localScale} "
                      + $"_transparancy(shared)={icon.SR.sharedMaterial.GetFloat(TransparancyProperty)} "
                      + $"ours={VisibleTransparancy}");
        }
    }

    private static void HideAll()
    {
        for (int i = 0; i < _markers.Count; i++)
        {
            SetActive(_markers[i], false);
        }
    }

    private static void SetActive(SpriteRenderer marker, bool active)
    {
        if (marker != null && marker.gameObject.activeSelf != active)
        {
            marker.gameObject.SetActive(active);
        }
    }
}
