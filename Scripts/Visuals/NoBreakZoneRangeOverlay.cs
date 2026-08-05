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

    // Reused every frame — the boundary walk writes into these rather than allocating (see
    // NoBreakZoneRange.WriteBoundaryTiles). Sized for the default range; a bigger radius from Conf/
    // in 7단계 truncates rather than overruns.
    private static int[] _tileX = new int[NoBreakZoneRange.BoundaryTileCount(
        NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneRange.DefaultDiameter))];
    private static int[] _tileZ = new int[NoBreakZoneRange.BoundaryTileCount(
        NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneRange.DefaultDiameter))];

    private static ObjectID _lensObjectID = ObjectID.None;
    private static bool _warnedNoSprite;

    /// Called from NoBreakZoneMod.ModObjectLoaded for every asset in the bundle. Matching a sprite
    /// by name is how the reference mod picks up its own helper sprites — the mod never gets a path
    /// or a guid at runtime, only the loaded objects.
    public static void RegisterLoadedObject(Object obj)
    {
        if (obj is Sprite sprite && sprite.name == MarkerSpriteName)
        {
            _markerSprite = sprite;
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

        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneRange.DefaultDiameter);
        int2 player = PlayerTile();
        int used = 0;

        for (int i = 0; i < pylons.Length; i++)
        {
            int2 pylon = pylons[i];

            // Skip a pylon whose whole square is out of reach before walking 80 tiles of it.
            if (math.abs(pylon.x - player.x) > DrawRadiusTiles + radius ||
                math.abs(pylon.y - player.y) > DrawRadiusTiles + radius)
            {
                continue;
            }

            int count = NoBreakZoneRange.WriteBoundaryTiles(pylon.x, pylon.y, radius, _tileX, _tileZ);
            for (int t = 0; t < count; t++)
            {
                if (math.abs(_tileX[t] - player.x) > DrawRadiusTiles ||
                    math.abs(_tileZ[t] - player.y) > DrawRadiusTiles)
                {
                    continue;
                }

                PlaceMarker(used++, _tileX[t], _tileZ[t]);
            }
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
    }

    private static bool ShouldDraw()
    {
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

        return player.visuallyEquippedContainedObject.objectData.objectID == _lensObjectID;
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

    private static void PlaceMarker(int index, int tileX, int tileZ)
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
        // by default, so it is rotated a quarter turn about X. The small lift avoids z-fighting with
        // the floor.
        marker.transform.SetPositionAndRotation(
            new Vector3(tileX, 0.02f, tileZ), Quaternion.Euler(90f, 0f, 0f));
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
            return;
        }

        renderer.sharedMaterial = icon.SR.sharedMaterial;
        renderer.sortingLayerID = icon.SR.sortingLayerID;
        // Below the placement icon itself: the outline is background information, and should never
        // sit on top of what the player is actively aiming.
        renderer.sortingOrder = icon.SR.sortingOrder - 1;
        renderer.maskInteraction = icon.SR.maskInteraction;
        renderer.gameObject.layer = icon.SR.gameObject.layer;
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
