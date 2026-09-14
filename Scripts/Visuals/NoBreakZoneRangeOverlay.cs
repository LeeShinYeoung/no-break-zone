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
//
// TWO COORDINATE SPACES, AND THE MARKERS WERE IN THE WRONG ONE FOR A MONTH. Every position the
// simulation hands out — a pylon's tile, the player's LocalToWorld — is in world space. Nothing the
// player sees is drawn there. The game keeps a render origin at the camera's rounded position
// (CameraManager.RenderOrigo, refreshed as the camera moves) and every visible transform sits at
// world minus that origin, so that what is on screen is always near zero and float precision holds
// however far from spawn the world grows. PlacementIcon shows the seam in one line:
//
//     Vector3 vec = Manager.camera.RenderOrigo + transform.position;   // tile = origin + render
//
// The markers were placed at raw tile coordinates: material, sorting, rotation, colour, height and
// transparency all copied from an icon that renders, and they still sat a player's-world-position
// away from anything the camera could see. EntityMonoBehaviour.ToRenderFromWorld is the game's own
// conversion and is what PlaceEdge goes through now; PlayerTile reads the world side for the same
// reason, since the player's transform is a render-space transform too.
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

    // Markers made on world entry rather than in the frame the lens first comes up: four pylons'
    // worth, the count at which a player first reported the lens stuttering (2026-09-14). More are
    // still made on demand, but by then making one is only a GameObject and a SpriteRenderer.
    private const int PrewarmedMarkers = 16;

    private static ObjectID _lensObjectID = ObjectID.None;
    private static bool _warnedNoSprite;

    // The look every marker copies from the game's placement icon, resolved ONCE (ResolveAppearance).
    // It used to be looked up per marker by scanning every loaded object, pooled inactive ones
    // included, and that was the lens's hitch: four pylons meant sixteen scans in a single frame.
    private static bool _appearanceResolved;
    private static bool _haveAppearance;
    private static Material _markerMaterial;
    private static int _markerSortingLayerID;
    private static int _markerSortingOrder;
    private static SpriteMaskInteraction _markerMaskInteraction;
    private static int _markerLayer;
    private static bool _warnedNoIcon;
    private static Color _markerColour = Color.white;

    // Following the render origin within the frame it moves (OnRenderOrigoMoved). One cached
    // delegate, so the -= in Dispose removes exactly what += added. System.Action is spelled out:
    // a `using System;` would make every `Object` here ambiguous with UnityEngine.Object.
    private static readonly System.Action OriginMovedHandler = OnRenderOrigoMoved;
    private static CameraManager _subscribedCamera;
    private static Vector3Int _placedOrigo;

    /// PlaceIconAmplify is driven by a shader float the game names "_transparancy": PlacementIcon
    /// writes it every frame on its own material INSTANCE, ramping 0 -> 0.5 while the player stands
    /// still and back to 0 when they move, so 0 is invisible and 0.5 is as shown as the game ever
    /// shows it.
    ///
    /// This was suspected of being the fault and was not: the shared asset we copy already stored
    /// 0.5 when logged (2026-09-10). It is pinned anyway, because a marker that depends on what the
    /// game's asset happens to store is one game update from vanishing again.
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
        EnsureWorldReady();

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

        // Every marker is placed against this frame's origin with the root back at zero. If the
        // origin moves later in this same frame, OnRenderOrigoMoved offsets the root by the
        // difference, so the outline moves with the floor instead of a frame behind it.
        EnsureRoot();
        _root.localPosition = Vector3.zero;
        _placedOrigo = Manager.camera.RenderOrigo;

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

        // Static one-shot flags fire once per PROCESS otherwise, and on 2026-09-10 that cost an
        // hour of reading an absent log line as "never happened". Reset with the markers so each
        // world reports afresh.
        _warnedNoIcon = false;

        // The borrowed look is resolved again after a reload rather than trusted across one.
        _appearanceResolved = false;
        _haveAppearance = false;
        _markerMaterial = null;

        // An unloaded mod must not leave a handler behind on the game's camera.
        FollowRenderOrigo(null);
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

        // visuallyEquippedContainedObject does report a held KeyItem — confirmed in the client log
        // on 2026-09-10, where holding the lens printed its ObjectID here.
        ObjectID held = player.visuallyEquippedContainedObject.objectData.objectID;
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
        // WorldPosition, not transform.position: the player's transform is a render-space object
        // like every other visible thing, and the pylon positions this is compared against come
        // from the simulation. Mixing the two only looked fine while the base sat near spawn, where
        // the render origin happens to be small.
        Vector3 position = Manager.main.player.WorldPosition;
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

        // RENDER SPACE. centreX/centreZ are tile coordinates from the simulation; the transform
        // has to be told where that is relative to the camera's render origin, or the marker lands
        // a base's-distance-from-spawn away from the screen (see the class comment). The game's
        // own helper does the subtraction, and it is re-done every frame here because the origin
        // moves with the camera.
        //
        // Flat on the ground, like the game's own tile-level sprites: the sprite quad stands upright
        // by default, so it is rotated a quarter turn about X — the same (90, 0, 0) PlacementIcon
        // reports for itself. The extra turn about Z is what makes a side run along Z instead of X;
        // adjust it first if the sides come out crossed. The small lift avoids z-fighting with the
        // floor.
        //
        // LOCAL to the root, which Update has just put back at zero. The root is what
        // OnRenderOrigoMoved shifts, so the marker's own position stays the plain render position.
        Vector3 renderPosition = EntityMonoBehaviour.ToRenderFromWorld(
            new Vector3(centreX, 0.02f, centreZ));
        marker.transform.localPosition = renderPosition;
        marker.transform.localRotation = Quaternion.Euler(90f, 0f, horizontal ? 0f : 90f);

        marker.transform.localScale = new Vector3(length, 1f, 1f);
        marker.color = _markerColour;
        SetActive(marker, true);
    }

    private static SpriteRenderer CreateMarker()
    {
        EnsureRoot();

        var markerObject = new GameObject("RangeMarker");
        markerObject.transform.SetParent(_root, false);

        var renderer = markerObject.AddComponent<SpriteRenderer>();
        renderer.sprite = _markerSprite;
        ApplyAppearance(renderer);

        markerObject.SetActive(false);
        return renderer;
    }

    private static void EnsureRoot()
    {
        if (_root != null)
        {
            return;
        }

        var rootObject = new GameObject("NoBreakZoneRangeOverlay");
        Object.DontDestroyOnLoad(rootObject);
        _root = rootObject.transform;
    }

    /// Everything that would otherwise happen in the frame the lens first comes up, done in the
    /// first frames of a world instead. A player reported a brief stutter on raising the lens with
    /// four or more pylons out (2026-09-14); after this, that frame has nothing heavy left in it.
    private static void EnsureWorldReady()
    {
        var manager = Manager.main;
        if (manager == null || manager.player == null)
        {
            return;
        }

        FollowRenderOrigo(Manager.camera);

        if (!_appearanceResolved)
        {
            ResolveAppearance();
        }

        // Nothing to prewarm for a player who has switched the display off, or before the sprite
        // the markers are drawn with has arrived.
        if (!NoBreakZoneConfig.ShowRangeWithLens || _markerSprite == null)
        {
            return;
        }

        while (_markers.Count < PrewarmedMarkers)
        {
            _markers.Add(CreateMarker());
        }
    }

    /// The game's floor tiles keep up with the render origin by listening for exactly this —
    /// MultiPugMap subscribes to CameraManager.RenderOrigoUpdated — and the outline now does too.
    private static void FollowRenderOrigo(CameraManager camera)
    {
        // Plain reference comparison, not Unity's ==: a destroyed camera still has to be
        // unsubscribed from, and its delegate field is managed memory that outlives the native side.
        if (ReferenceEquals(camera, _subscribedCamera))
        {
            return;
        }

        if (!ReferenceEquals(_subscribedCamera, null))
        {
            _subscribedCamera.RenderOrigoUpdated -= OriginMovedHandler;
        }

        _subscribedCamera = camera;
        if (!ReferenceEquals(camera, null))
        {
            camera.RenderOrigoUpdated += OriginMovedHandler;
        }
    }

    /// WHY THE OUTLINE JUMPED A TILE WHILE WALKING. Within a frame, IMod.Update — and so Update here —
    /// runs in MonoBehaviour Update. The client world's SimulationSystemGroup runs after it, at the
    /// end of Unity's Update phase, and UpdateGraphicalObjectTransformSystem there calls
    /// CameraManager.UpdateRenderOrigo. The camera is applied later still, in PresentationSystemGroup.
    /// So in every frame where the rounded camera position crossed into a new tile, the markers had
    /// been placed against the old origin and were drawn a tile off. It happened only while moving,
    /// never standing still (a tester and the user, 2026-09-14).
    ///
    /// Absolute, never cumulative: this also fires from UpdateSceneHandler when a scene starts, and
    /// it may fire more than once before the next Update. The sign is the game's own, from
    /// MoveRenderAnchors: `position -= newOrigo - oldOrigo`.
    private static void OnRenderOrigoMoved()
    {
        if (_root == null || ReferenceEquals(_subscribedCamera, null))
        {
            return;
        }

        Vector3Int origo = _subscribedCamera.RenderOrigo;
        _root.localPosition = new Vector3(_placedOrigo.x - origo.x, 0f, _placedOrigo.z - origo.z);
    }

    // A bare SpriteRenderer has no material that suits this game's render pipeline, and unlike the
    // reference mod we are not placing an object, so there is no PlacementIcon handed to us to copy
    // from. Borrowing the game's own keeps the marker on the game's sorting layer with the game's
    // material; without it, Unity's default sprite material is what gets used and the marker may be
    // invisible or drawn through walls.
    //
    // Everything copied here was logged against the icon on 2026-09-10 and matched. This was the
    // prime suspect for the invisible lens through three play sessions and was innocent all along;
    // the fault was the coordinate space (class comment), which no amount of appearance can fix.
    //
    // ONCE, AND FROM THE POOL'S PREFAB RATHER THAN A SCENE SCAN. This used to run
    // FindAnyObjectByType<PlacementIcon>(FindObjectsInactive.Include) for every marker made. That
    // walks every loaded object, and MemoryManager keeps inactive copies of every poolable prefab
    // from boot, so with four pylons it was sixteen long walks in the frame the lens came up — the
    // stutter a player reported (2026-09-14). The icon the player sees belongs to an equipment slot
    // taken from one of those pools (PlayerController asks Manager.memory for a PlaceObjectSlot), so
    // the pooled prefab carries the same look behind public fields. The scan stays as a fallback.
    private static void ResolveAppearance()
    {
        // Set first: a failure is remembered too, so the fallback scan runs at most once.
        _appearanceResolved = true;

        SpriteRenderer source = FindIconInPools();
        if (source == null)
        {
            // FindAnyObjectByType, not FindObjectOfType: the latter is deprecated and the build
            // warned about it (CS0618). FindObjectsInactive.Include because the icon is inactive
            // whenever the player is not placing something, which is most of the time.
            var icon = Object.FindAnyObjectByType<PlacementIcon>(FindObjectsInactive.Include);
            source = icon != null ? icon.SR : null;
        }

        if (source == null)
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

        // The prefab's sharedMaterial is the game's own asset. A live icon's becomes a per-icon copy
        // once PlacementIcon.LateUpdate touches SR.material, and that copy goes when the icon does —
        // one more reason the prefab is the better source.
        _markerMaterial = source.sharedMaterial;
        _markerSortingLayerID = source.sortingLayerID;
        // Above the placement icon rather than below it. Below was the tidier choice — an outline is
        // background information — but it also put the marker behind whatever the game draws at
        // ground level, which is one of the two ways it could have gone missing. Order first, taste
        // afterwards.
        _markerSortingOrder = source.sortingOrder + 1;
        _markerMaskInteraction = source.maskInteraction;
        _markerLayer = source.gameObject.layer;
        // COLOUR IS COPIED TOO. On a material that multiplies by vertex colour, the tint a fresh
        // SpriteRenderer defaults to can alone render a sprite invisible.
        _markerColour = source.color;
        _haveAppearance = true;
    }

    /// The placement icon on the pooled PlaceObjectSlot prefab, or null.
    ///
    /// WaterCanSlot, BucketSlot and PaintToolSlot derive from PlaceObjectSlot and bring placement
    /// handlers of their own, so they are skipped. `is` rather than comparing GetType(): the mod
    /// safety check rejects GetType outright (see RegisterLoadedObject).
    private static SpriteRenderer FindIconInPools()
    {
        var memory = Manager.memory;
        if (memory == null || memory.poolablePrefabBanks == null)
        {
            return null;
        }

        foreach (PoolablePrefabBank bank in memory.poolablePrefabBanks)
        {
            if (bank == null)
            {
                continue;
            }

            foreach (PoolablePrefabBank.PoolablePrefab entry in bank)
            {
                if (entry == null || entry.prefab == null)
                {
                    continue;
                }

                var slot = entry.prefab.GetComponent<PlaceObjectSlot>();
                if (slot == null || slot is WaterCanSlot || slot is BucketSlot || slot is PaintToolSlot)
                {
                    continue;
                }

                PlacementHandler handler = slot.placementHandler;
                PlacementIcon icon = handler != null ? handler.placeableIcon : null;
                if (icon != null && icon.SR != null)
                {
                    return icon.SR;
                }
            }
        }

        return null;
    }

    private static void ApplyAppearance(SpriteRenderer renderer)
    {
        if (!_haveAppearance)
        {
            return;  // ResolveAppearance has already said so in the log
        }

        renderer.sharedMaterial = _markerMaterial;
        renderer.sortingLayerID = _markerSortingLayerID;
        renderer.sortingOrder = _markerSortingOrder;
        renderer.maskInteraction = _markerMaskInteraction;
        renderer.gameObject.layer = _markerLayer;

        // A property block rather than renderer.material: the latter clones the material per
        // marker and the clones outlive the GameObjects Dispose destroys, so a mod reload would
        // leak four materials a time. A block overrides the float on this renderer only, leaves
        // the shared asset alone for the game's own icon, and costs nothing to drop.
        _propertyBlock ??= new MaterialPropertyBlock();
        renderer.GetPropertyBlock(_propertyBlock);
        _propertyBlock.SetFloat(TransparancyProperty, VisibleTransparancy);
        renderer.SetPropertyBlock(_propertyBlock);
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
