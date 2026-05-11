using System.Collections.Generic;
using UnityEngine;

// Conditionally renders a small "shaft stub" sprite at each of a building's power ports —
// only when a compatible shaft is actually wired up at that port's offset. Without this,
// power buildings that bake a shaft visualization into their base sprite (windmill, wheel)
// would always show an axle protruding into thin air, even on isolated placements.
//
// Spawned by Structure.AttachPortStubs(). Stubs are created lazily — Init records each
// port's metadata but the child SpriteRenderers are only spawned when a compatible shaft
// is detected, and torn down when the shaft is removed. Keeps the GameObject count tight
// for ports that never see a connection.
//
// Stubs sit one sortingOrder behind the building (so they read as "the building's axle
// poking out from underneath the connecting shaft tile") and on the BUILDING tile adjacent
// to the shaft — not on the shaft tile itself.
//
// Refresh fires on PowerSystem.onTopologyRebuilt (covers shaft placements/removals) and
// once on Start (covers initial placement and load).
//
// Sprite assets:
//   port_shaft_h.png — horizontal port stub. Authored extending LEFTWARD from a connection
//                      at the sprite's right edge. flipX'd for ports on the building's
//                      right side.
//   port_shaft_v.png — vertical port stub. Authored extending DOWNWARD from a connection
//                      at the sprite's top edge. flipY'd for ports on the building's
//                      top side.
//
// Per-port handling:
//   Axis.Horizontal → always uses port_shaft_h.
//   Axis.Vertical   → always uses port_shaft_v.
//   Axis.Both       → side inferred from port position. Ports on left/right of the
//                     footprint use port_shaft_h; ports on top/bottom use port_shaft_v.
//                     Corner-style ports (outside on both axes) and inside ports are
//                     skipped — the visual would be ambiguous.
//
// If a sprite asset is missing the corresponding stubs are silently skipped — keeps the
// system optional per building. Mirroring is baked into the per-stub flipX at Init time
// (effectiveDx already accounts for it), so flipX/flipY don't need to be recomputed on
// refresh.
public class PortStubVisuals : MonoBehaviour {
    class Stub {
        public int dx;
        public int dy;
        public PowerSystem.Axis axis;  // original port axis — used for HasCompatibleShaftAt
        public Sprite sprite;
        public bool flipX;
        public bool flipY;
        public int stubDx;             // pre-clamped target tile (mirroring already baked in)
        public int stubDy;
        public GameObject go;          // null until first connected
    }

    Structure anchor;
    int parentOrder;
    readonly List<Stub> stubs = new();

    // Cache loaded sprites once per session — avoids re-hitting Resources.Load on every
    // building placement. Null entries signal "asset not authored" and prevent repeated
    // load attempts.
    static Sprite _hSprite;
    static Sprite _vSprite;
    static bool _spritesLoaded;

    // Reload-Domain-off support — see SpriteMaterialUtil.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() {
        _hSprite = null; _vSprite = null; _spritesLoaded = false;
    }

    static void EnsureSpritesLoaded() {
        if (_spritesLoaded) return;
        _spritesLoaded = true;
        _hSprite = Resources.Load<Sprite>("Sprites/Buildings/port_shaft_h");
        _vSprite = Resources.Load<Sprite>("Sprites/Buildings/port_shaft_v");
    }

    public void Init(Structure anchor, IEnumerable<PowerSystem.PowerPort> ports) {
        this.anchor = anchor;
        this.parentOrder = anchor.sr != null ? anchor.sr.sortingOrder : 10;
        EnsureSpritesLoaded();
        if (ports == null) return;

        int nx = anchor.structType.nx;
        int ny = Mathf.Max(1, anchor.structType.ny);
        bool mirrored = anchor.mirrored;

        foreach (PowerSystem.PowerPort port in ports) {
            // Apply the same X mirror as PowerSystem.FindAttachedNetwork so the stub
            // sits against the correct side after F-flipping the building.
            int effectiveDx = mirrored ? (nx - 1 - port.dx) : port.dx;
            bool toLeft   = effectiveDx < 0;
            bool toRight  = effectiveDx >= nx;
            bool toBottom = port.dy < 0;
            bool toTop    = port.dy >= ny;

            // Pick which sprite to use, and which way it should flip so it extends OUTWARD
            // from the building. Sprite art convention: h extends left, v extends down.
            Sprite sprite = null;
            bool flipX = false;
            bool flipY = false;
            switch (port.axis) {
                case PowerSystem.Axis.Horizontal:
                    sprite = _hSprite;
                    flipX = toRight;
                    break;
                case PowerSystem.Axis.Vertical:
                    sprite = _vSprite;
                    flipY = toTop;
                    break;
                default: // Axis.Both
                    bool xSide = toLeft || toRight;
                    bool ySide = toBottom || toTop;
                    if (xSide && !ySide) {
                        sprite = _hSprite;
                        flipX = toRight;
                    } else if (ySide && !xSide) {
                        sprite = _vSprite;
                        flipY = toTop;
                    }
                    // else: corner or inside-the-footprint port — ambiguous, skip.
                    break;
            }
            if (sprite == null) continue;

            // Clamp to building's outermost tile in the port direction — stub renders on
            // the BUILDING tile adjacent to the shaft, not on the shaft tile itself.
            int stubDx = Mathf.Clamp(effectiveDx, 0, nx - 1);
            int stubDy = Mathf.Clamp(port.dy,     0, ny - 1);

            stubs.Add(new Stub {
                dx = port.dx, dy = port.dy, axis = port.axis,
                sprite = sprite, flipX = flipX, flipY = flipY,
                stubDx = stubDx, stubDy = stubDy,
            });
        }

        if (PowerSystem.instance != null)
            PowerSystem.instance.onTopologyRebuilt += Refresh;
    }

    void Start() {
        // Start may run a frame after Init (when the building is placed) or even on the
        // same frame; either way, refresh once so initial state matches current connectivity.
        Refresh();
    }

    void OnDestroy() {
        if (PowerSystem.instance != null)
            PowerSystem.instance.onTopologyRebuilt -= Refresh;
        // Child stub GOs are destroyed automatically with this GO's parent, no manual cleanup.
    }

    void Refresh() {
        if (anchor == null || PowerSystem.instance == null) return;
        int nx = anchor.structType.nx;
        bool mirrored = anchor.mirrored;
        foreach (Stub stub in stubs) {
            int dxAbs = mirrored ? (nx - 1 - stub.dx) : stub.dx;
            int tx = anchor.x + dxAbs;
            int ty = anchor.y + stub.dy;
            bool connected = PowerSystem.instance.HasCompatibleShaftAt(tx, ty, stub.axis);
            if (connected && stub.go == null) {
                Spawn(stub);
            } else if (!connected && stub.go != null) {
                Destroy(stub.go);
                stub.go = null;
            }
        }
    }

    void Spawn(Stub stub) {
        GameObject stubGO = new GameObject($"port_stub_{stub.dx}_{stub.dy}");
        stubGO.transform.SetParent(transform, true);
        stubGO.transform.position = new Vector3(anchor.x + stub.stubDx, anchor.y + stub.stubDy, 0f);

        SpriteRenderer ssr = SpriteMaterialUtil.AddSpriteRenderer(stubGO);
        ssr.sprite = stub.sprite;
        ssr.flipX = stub.flipX;
        ssr.flipY = stub.flipY;
        ssr.sortingOrder = parentOrder - 1; // behind the building
        LightReceiverUtil.SetSortBucket(ssr);

        stub.go = stubGO;
    }
}
