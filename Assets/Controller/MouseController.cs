using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Reflection;

// note: this is not really a controller. it just manages mouse input

public class MouseController : MonoBehaviour {
    public enum MouseMode {Select, Build, Remove, Harvest};
    public MouseMode mouseMode = MouseMode.Select;
    public GameObject buildPreview;
    SpriteRenderer buildPreviewSr;
    Sprite buildPreviewDefaultSprite;
    // Build-mode preview state.
    //
    // The inspector-attached `buildPreviewSr` on `buildPreview` is the cursor sprite
    // for Remove/Harvest modes (and the no-StructType fallback in Build mode). When
    // the player picks a structType in Build mode, we disable the cursor SR and let
    // StructureVisualBuilder spawn the actual primary visual under `previewVisualRoot`
    // — same code path as Structure ctor and Blueprint ctor, just with sortingOrder 200
    // and a translucent tint. Spawned visuals are pooled across the same StructType /
    // shape / mirror / rotation tuple — only respawned when something material changes.
    GameObject previewVisualRoot;
    StructType cachedPreviewSt;
    int        cachedPreviewShapeIndex;
    bool       cachedPreviewMirrored;
    int        cachedPreviewRotation;
    // Persistent ghost shown at the first-clicked endpoint of a two-click
    // placement (rope bridge). Lives separate from previewVisualRoot — that
    // one tracks the cursor; this one stays parked at the chosen tile until
    // the second click commits or BuildPanel.firstEndpoint is cleared.
    GameObject firstEndpointGhostRoot;
    StructType cachedFirstEndpointSt;
    // Ghost catenary shown between firstEndpoint and the hovered tile during
    // a two-click placement's second-click phase. Rebuilt when the cursor
    // moves to a new tile (cheap — sprite chain of ~20 SRs for a typical
    // bridge). Cleared when firstEndpoint clears, when the cursor leaves
    // the world, or when the same-tile cache hit lets us skip.
    GameObject ghostCatenaryRoot;
    Tile cachedGhostEnd;
    Tile cachedGhostStart;
    StructType cachedGhostSt;
    Vector3 prevPosition;

    public World world;
    public static MouseController instance { get; protected set; }
    Component ppcComponent;
    PropertyInfo ppcAssetsPPU;

    // --- drag-select state ---
    // Shared across Select and Harvest modes — the input-time semantics differ (Select picks
    // storage inventories; Harvest flags plants) but the threshold/visual-rect machinery is identical.
    private Vector3 _dragStartScreenPos;
    private bool _isDragging = false;
    private MouseMode? _dragStartedInMode = null;
    private const float DragThresholdPixels = 8f;
    [SerializeField] private RectTransform dragRectTransform;

    // RMB click-vs-drag detection. RMB-drag pans the camera; an RMB without
    // significant movement is treated as a "cancel" click that exits
    // Remove/Harvest modes back to Select. Reuses DragThresholdPixels.
    private Vector3 _rmbDownScreenPos;

    // --- debug cursor light (Ctrl+T toggles SetActive) ---
    // Wire a GameObject with a LightSource (and optional sortOrderOverride) in the
    // inspector. Keep it disabled by default so it only appears when toggled on.
    [SerializeField] private GameObject debugCursorLight;

    // ── Camera pan tuning ──────────────────────────────────────────────────
    // Pan speed in screen-heights per second — independent of zoom, so a value
    // of 1.0 traverses one full viewport height per second at any zoom level.
    // Same magnitude on x and y (no aspect scaling) so motion feels symmetric.
    // RMB drag stays the primary pan; these are secondary inputs.
    [SerializeField] float panSpeedScreens = 0.6f;
    [SerializeField] float edgePanInsetPx = 16f;  // dead zone from each screen edge that triggers edge-pan
    [SerializeField] bool edgePanEnabled = true;
    // Time (seconds) to ramp from rest → full speed when input starts, and
    // full speed → rest when input stops. Smooths out keypress jerk and lets
    // motion coast for a beat after release.
    [SerializeField] float panRampTime = 0.1f;
    private Vector2 _panVelocity;  // current pan velocity in screen-heights per second

    // ── Click-to-follow ────────────────────────────────────────────────────
    // Clicking an already-selected animal makes the camera track it exactly
    // (no lerp). Selection is preserved so the indicator stays visible.
    // Any pan input (WASD, edge, or RMB drag) cancels follow.
    //
    // Snap runs in LateUpdate, not Update, so it reads the animal's freshly-
    // updated position for the frame. Unity doesn't guarantee an Update order
    // between MonoBehaviours, so doing it in Update causes the camera to track
    // a stale position on ~half of frames — visible as a one-pixel jitter
    // under the Pixel Perfect Camera.
    private Animal _followAnimal;

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one mouse controller");}
        instance = this;
        buildPreviewSr = buildPreview.GetComponent<SpriteRenderer>();
        buildPreviewSr.sortingOrder = 200;
        LightReceiverUtil.SetSortBucket(buildPreviewSr);
        buildPreviewDefaultSprite = buildPreviewSr.sprite;
        // Child container that holds the StructureVisualBuilder-spawned preview SRs.
        // Toggled active/inactive as a unit when entering/leaving Build mode.
        previewVisualRoot = new GameObject("buildPreviewVisualRoot");
        previewVisualRoot.transform.SetParent(buildPreview.transform, false);
        previewVisualRoot.SetActive(false);
        foreach (var c in Camera.main.GetComponents<Component>()) {
            var prop = c.GetType().GetProperty("assetsPPU");
            if (prop != null) { ppcComponent = c; ppcAssetsPPU = prop; break; }
        }
    }

    void Update() {
        // Esc handling lives in UI.Update — see the priority chain there. Build subpanel
        // and exclusive panels get first dibs before we fall through to leaving the mode.
        if (Input.GetKeyDown(KeyCode.F) && mouseMode == MouseMode.Build)
            BuildPanel.instance?.ToggleMirror();
        if (Input.GetKeyDown(KeyCode.R) && mouseMode == MouseMode.Build)
            BuildPanel.instance?.ToggleRotate();
        // Shape variant cycling — Q shrinks (delta -1), E grows (delta +1). Gated on the
        // current StructType declaring shapes, so it's a no-op for non-shape buildings.
        if (Input.GetKeyDown(KeyCode.E) && mouseMode == MouseMode.Build)
            BuildPanel.instance?.CycleShape(+1);
        if (Input.GetKeyDown(KeyCode.Q) && mouseMode == MouseMode.Build)
            BuildPanel.instance?.CycleShape(-1);
        // Ctrl+Alt+B = arm one-shot instant-build for the next blueprint placed.
        // Symmetric to InfoPanel's Ctrl+Shift+D instant-deconstruct. Uses Alt (not Shift)
        // to dodge Unity's Ctrl+Shift+B Build Settings shortcut. Not gated on Build mode
        // so the user can arm it before selecting a building.
        if (Input.GetKeyDown(KeyCode.B)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt))) {
            BuildPanel.instantBuildNext = !BuildPanel.instantBuildNext;
            Debug.Log($"[debug] instant-build next blueprint: {(BuildPanel.instantBuildNext ? "ARMED" : "disarmed")}");
        }
        // Ctrl+D = audit dump. Excludes Shift so it doesn't double-fire with
        // InfoPanel's Ctrl+Shift+D instant-deconstruct shortcut.
        if (Input.GetKeyDown(KeyCode.D)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) {
            WorkOrderManager.instance?.AuditOrders();
            InventoryController.instance?.ValidateGlobalInventory();
            var ws = WeatherSystem.instance;
            if (ws != null)
                Debug.Log($"Temperature: {ws.temperature:F1}°C, Season: {ws.GetSeason()} (day {ws.GetDayOfYear():F1}/{World.daysInYear}), Humidity: {ws.humidity:F2} (raining: {ws.isRaining}), Wind: {ws.wind:F2}");
        }

        // Debug cursor light toggle
        if (Input.GetKeyDown(KeyCode.T) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) {
            if (debugCursorLight == null) {
                Debug.LogError("Ctrl+T: debugCursorLight is not wired on MouseController — assign a LightSource GameObject in the inspector.");
            } else {
                debugCursorLight.SetActive(!debugCursorLight.activeSelf);
            }
        }
        if (debugCursorLight != null && debugCursorLight.activeSelf) {
            Vector3 mouseWorld = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            debugCursorLight.transform.position = new Vector3(mouseWorld.x, mouseWorld.y, 0f);
        }

        // Secondary pan inputs (WASD + screen edge). Runs before the overUI return
        // so keyboard pan still fires when the cursor is parked on a UI panel.
        HandleCameraPan();

        bool overUI = EventSystem.current.IsPointerOverGameObject();
        if (overUI && !_isDragging) {
            // Clicking any UI element while a tool mode is active exits back to Select.
            // Lets the player retarget by clicking a different toolbar button (or any
            // panel) without first pressing Esc.
            if (Input.GetMouseButtonDown(0)
                && (mouseMode == MouseMode.Build
                    || mouseMode == MouseMode.Remove
                    || mouseMode == MouseMode.Harvest))
                SetModeSelect();
            return;
        }
        if (world == null){
            world = WorldController.instance.world;
        }

        // zoom
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0f && ppcAssetsPPU != null) {
            int[] zoomLevels = {8, 12, 16, 24 }; // in PPU
            int currentPPU = (int)ppcAssetsPPU.GetValue(ppcComponent);
            int idx = System.Array.IndexOf(zoomLevels, currentPPU);
            if (idx == -1) idx = System.Array.BinarySearch(zoomLevels, currentPPU);
            if (idx < 0) idx = ~idx;
            idx = Mathf.Clamp(idx + (scroll > 0 ? 1 : -1), 0, zoomLevels.Length - 1);
            int newPPU = zoomLevels[idx];
            if (newPPU != currentPPU) {
                // Capture the world point under the cursor BEFORE the zoom change, then
                // shift the camera so that same world point lands back under the cursor
                // at the new ortho size. Falls back to centered zoom if the cursor is
                // outside the game window.
                Vector3 mouseScreen = Input.mousePosition;
                bool cursorInWindow = mouseScreen.x >= 0f && mouseScreen.x <= Screen.width
                                   && mouseScreen.y >= 0f && mouseScreen.y <= Screen.height;

                ppcAssetsPPU.SetValue(ppcComponent, newPPU);
                // PPC updates orthographicSize in its own LateUpdate, so estimate the new
                // half-height by scaling the current size by the PPU ratio (exact if zoom factor is stable).
                float oldHalfH = Camera.main.orthographicSize;
                float estimatedHalfH = oldHalfH * currentPPU / newPPU;

                if (cursorInWindow) {
                    // Pixel-to-world scale = 2*halfH / Screen.height. Offset of cursor
                    // from screen centre, in pixels, times the change in scale, is how
                    // much the cursor's world point would drift if we didn't compensate.
                    float pixelToWorld = 2f / Screen.height;
                    float dxPixels = mouseScreen.x - Screen.width  * 0.5f;
                    float dyPixels = mouseScreen.y - Screen.height * 0.5f;
                    float shiftX = dxPixels * pixelToWorld * (oldHalfH - estimatedHalfH);
                    float shiftY = dyPixels * pixelToWorld * (oldHalfH - estimatedHalfH);
                    Vector3 p = Camera.main.transform.position;
                    p.x += shiftX;
                    p.y += shiftY;
                    Camera.main.transform.position = p;
                }
                ClampCameraToWorld(estimatedHalfH);
            }
        }

        // draggin world around
        Vector3 currPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        currPosition.z = 1f;
        if (Input.GetMouseButtonDown(1)){
            prevPosition = Input.mousePosition;
            _rmbDownScreenPos = Input.mousePosition;
        }
        if (Input.GetMouseButton(1)) {
            Vector3 currScreenPosition = Input.mousePosition;
            Vector3 delta = Camera.main.ScreenToWorldPoint(prevPosition)
                        - Camera.main.ScreenToWorldPoint(currScreenPosition);
            delta.z = 0f;
            Camera.main.transform.Translate(delta);
            prevPosition = currScreenPosition;
            ClampCameraToWorld();
        }
        // RMB-up with no significant movement = a click, not a pan. Exit Remove/Harvest
        // back to Select. Build-mode RMB cancel was tried previously and disabled
        // (see commit history) so it's deliberately omitted here.
        if (Input.GetMouseButtonUp(1)
            && (mouseMode == MouseMode.Remove || mouseMode == MouseMode.Harvest)
            && Vector3.Distance(Input.mousePosition, _rmbDownScreenPos) <= DragThresholdPixels) {
            SetModeSelect();
        }

        // set buildPreview if in build/remove mode
        Tile tileAt = WorldController.instance.world.GetTileAt(currPosition.x, currPosition.y);
        StructType st = BuildPanel.instance != null ? BuildPanel.instance.structType : null;

        // For multi-tile buildings, offset anchor so the building is centered on the mouse
        Tile anchorTile = tileAt;
        if (tileAt != null && st != null && st.nx > 1) {
            int offsetX = (st.nx - 1) / 2;
            anchorTile = WorldController.instance.world.GetTileAt(tileAt.x - offsetX, tileAt.y);
        }

        if (tileAt == null){ buildPreview.SetActive(false); }
        else if ((mouseMode == MouseMode.Build) || (mouseMode == MouseMode.Remove) || (mouseMode == MouseMode.Harvest)){
            buildPreview.SetActive(true);
            if (mouseMode == MouseMode.Build && st != null && anchorTile != null) {
                int shapeIndex = BuildPanel.instance != null ? BuildPanel.instance.shapeIndex : 0;
                bool mirrored  = BuildPanel.instance != null && BuildPanel.instance.mirrored;
                int rotation   = (BuildPanel.instance != null) ? BuildPanel.instance.rotation : 0;
                // Two-click placement (rope bridge), second-click hover: override the
                // F-key mirror with the geometry the built post will end up with. If the
                // cursor is to the LEFT of the firstEndpoint, this post becomes the LEFT
                // post of the bridge → mirrored=true. Matches Blueprint.Complete's
                // aIsLeft logic so the preview never disagrees with the build outcome.
                if (BuildPanel.instance != null
                        && BuildPanel.instance.firstEndpoint != null
                        && st.placementMethod == "twoClick") {
                    mirrored = anchorTile.x < BuildPanel.instance.firstEndpoint.x;
                }
                Shape shape    = st.GetShape(shapeIndex);
                bool shapeAware = st.HasShapes;

                // Respawn the preview visual whenever the choice of structType / shape /
                // mirror / rotation changes. Cheap — these change on user input (build menu
                // click, Q/E, F, R), not per frame. The transform.position update below
                // happens every frame regardless and never reallocates.
                if (cachedPreviewSt        != st
                    || cachedPreviewShapeIndex != shapeIndex
                    || cachedPreviewMirrored   != mirrored
                    || cachedPreviewRotation   != rotation) {
                    RebuildPreviewVisual(st, shape, mirrored, rotation);
                    cachedPreviewSt        = st;
                    cachedPreviewShapeIndex = shapeIndex;
                    cachedPreviewMirrored   = mirrored;
                    cachedPreviewRotation   = rotation;
                }

                // Inspector cursor SR off; spawned root on. Same toggle every frame is fine
                // — Unity short-circuits no-op enable changes.
                buildPreviewSr.enabled = false;
                previewVisualRoot.SetActive(true);

                // Position the parent each frame to follow the cursor. Shape-aware previews
                // anchor at the bottom-left tile centre (matching Structure / Blueprint ctor's
                // shape-aware origin); legacy multi-tile uses the centred-footprint helper.
                buildPreview.transform.position = shapeAware
                    ? new Vector3(anchorTile.x, anchorTile.y + (st.depth == 3 ? -1f/8f : 0f), -1f)
                    : StructureVisuals.PositionFor(st, anchorTile.x, anchorTile.y, z: -1);
                buildPreview.transform.localScale = Vector3.one;
                // Rotation lives on previewVisualRoot (Build sets it there) so the parent
                // stays at identity for clean position math.
                buildPreview.transform.rotation = Quaternion.identity;
            } else {
                // Non-Build (Remove/Harvest): hide the spawned visual root and restore the
                // inspector cursor SR with its default sprite. Reset the cache so re-entering
                // Build forces a fresh spawn (handles both StructType-changed and StructType-
                // null-then-non-null transitions).
                buildPreview.transform.position = new Vector3(tileAt.x, tileAt.y, -1);
                buildPreviewSr.enabled  = true;
                buildPreviewSr.sprite   = buildPreviewDefaultSprite;
                buildPreviewSr.flipX    = false;
                // Tint by tool: Remove=red, Harvest=green. Build-fallback (no
                // StructType yet) stays white so it doesn't imply a destructive action.
                if (mouseMode == MouseMode.Remove)
                    buildPreviewSr.color = new Color(0.8f, 0.2f, 0.2f, 1f);
                else if (mouseMode == MouseMode.Harvest)
                    buildPreviewSr.color = new Color(0.2f, 0.6f, 0.1f, 1f);
                else
                    buildPreviewSr.color = Color.white;
                buildPreviewSr.drawMode = SpriteDrawMode.Simple;
                buildPreview.transform.localScale = Vector3.one;
                buildPreview.transform.rotation = Quaternion.identity;
                previewVisualRoot.SetActive(false);
                cachedPreviewSt = null;
            }
        }
        if (mouseMode == MouseMode.Select){
            buildPreview.SetActive(false);
        }

        // First-endpoint ghost (two-click placement). Independent of the
        // cursor-following preview above — sits at BuildPanel.firstEndpoint
        // until the second click commits or the endpoint is cleared.
        UpdateFirstEndpointGhost(tileAt);
        // Ghost catenary between firstEndpoint and the hovered tile. Rebuilt
        // only when the (endpoint, cursor tile, structType) tuple changes.
        UpdateGhostCatenary(tileAt);


        // Shift+RMB on storage = paste filters (before drag handling consumes the click)
        Inventory storageHere = GetStorageAt(tileAt);
        if (Input.GetMouseButtonDown(1) && mouseMode == MouseMode.Select
            && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            && storageHere != null) {
            InventoryController.instance.PasteAllowed(storageHere);
        }

        // LMB down: record drag start for Select/Harvest modes; immediate action for Build/Remove
        if (Input.GetMouseButtonDown(0)) {
            if (mouseMode == MouseMode.Select || mouseMode == MouseMode.Harvest) {
                _dragStartScreenPos = Input.mousePosition;
                _isDragging = false;
                _dragStartedInMode = mouseMode;
            } else if (mouseMode == MouseMode.Build) {
                // No structType ⇒ click has no tool meaning; fall back to Select
                // (Esc-step-4 equivalent for the click). Seed the drag-start state
                // so the same press can drag-select on release without the user
                // needing to lift the mouse first.
                if (BuildPanel.instance == null || BuildPanel.instance.structType == null) {
                    SetModeSelect();
                    _dragStartScreenPos = Input.mousePosition;
                    _isDragging = false;
                    _dragStartedInMode = MouseMode.Select;
                } else {
                    Tile placeTile = anchorTile ?? tileAt;
                    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    if (BuildPanel.instance.PlaceBlueprint(placeTile) && !shift)
                        mouseMode = MouseMode.Select;
                }
            } else if (mouseMode == MouseMode.Remove) {
                BuildPanel.instance.Remove(tileAt);
            }
        }

        // LMB held in Select/Harvest mode: check drag threshold and update visual rect
        if (Input.GetMouseButton(0) && _dragStartedInMode.HasValue) {
            float dist = Vector3.Distance(Input.mousePosition, _dragStartScreenPos);
            if (!_isDragging && dist > DragThresholdPixels)
                _isDragging = true;
            if (dragRectTransform != null)
                dragRectTransform.gameObject.SetActive(_isDragging);
            if (_isDragging)
                UpdateDragRect(_dragStartScreenPos, Input.mousePosition);
        }

        // LMB up in Select mode: commit drag-select or handle as single click
        if (Input.GetMouseButtonUp(0) && mouseMode == MouseMode.Select) {
            if (dragRectTransform != null) dragRectTransform.gameObject.SetActive(false);
            if (_isDragging) {
                CommitDragSelect(_dragStartScreenPos, Input.mousePosition);
            } else {
                HandleSelectClick(tileAt, Camera.main.ScreenToWorldPoint(Input.mousePosition));
            }
            _isDragging = false;
            _dragStartedInMode = null;
        }

        // Harvest tool is paint-only — both paths set harvestFlagged=true, never toggle.
        if (Input.GetMouseButtonUp(0) && mouseMode == MouseMode.Harvest) {
            if (dragRectTransform != null) dragRectTransform.gameObject.SetActive(false);
            if (_isDragging) {
                CommitHarvestDrag(_dragStartScreenPos, Input.mousePosition);
            } else {
                FlagHarvestAt(tileAt);
            }
            _isDragging = false;
            _dragStartedInMode = null;
        }
    }

    private void FlagHarvestAt(Tile t) {
        if (t?.plant != null) t.plant.SetHarvestFlagged(true);
    }

    // Tears down the previous preview root and creates a fresh one for the given
    // (st, shape, mirrored, rotation) tuple. We destroy + recreate the root rather
    // than reusing it because StructureVisualBuilder.Build attaches the main SR
    // directly to its parent GameObject — Unity disallows adding a second SR to a
    // GameObject that already has one, and `Destroy` is deferred to end-of-frame so
    // we can't strip + re-add the component within a single frame. Hiding the old
    // root before destroying suppresses any flicker from the queued teardown.
    //
    // Called only when one of (st, shape, mirrored, rotation) changes — not per
    // frame — so the GC cost of recreating GOs is bounded by user input rate.
    private void RebuildPreviewVisual(StructType st, Shape shape, bool mirrored, int rotation) {
        if (previewVisualRoot != null) {
            previewVisualRoot.SetActive(false);
            Destroy(previewVisualRoot);
        }
        previewVisualRoot = new GameObject("buildPreviewVisualRoot");
        previewVisualRoot.transform.SetParent(buildPreview.transform, false);
        StructureVisualBuilder.Build(previewVisualRoot, st, shape, mirrored, rotation, 200,
                                     new Color(1f, 1f, 1f, 0.3f));
    }

    // Manages the persistent ghost shown at BuildPanel.firstEndpoint during a
    // two-click placement (rope bridge). The first click stashes the endpoint
    // but doesn't create a Blueprint yet — without a visible ghost the player
    // has no feedback that the click registered. We spawn a translucent post
    // sprite there and update its flipX based on cursor position relative to
    // the endpoint:
    //   cursor.x > endpoint.x → endpoint will be the LEFT post  → mirrored=true
    //   cursor.x < endpoint.x → endpoint will be the RIGHT post → mirrored=false
    // i.e. the ghost shows the same orientation the built post will have, so
    // there's no surprise flip on commit.
    //
    // Teardown fires when firstEndpoint is null (clear / commit / cancel) or
    // when leaving Build mode. Live structType swaps rebuild the ghost so the
    // sprite stays in sync.
    void UpdateFirstEndpointGhost(Tile cursorTile) {
        BuildPanel bp = BuildPanel.instance;
        Tile fe = bp?.firstEndpoint;
        StructType st = bp?.structType;
        bool shouldShow = fe != null && st != null && mouseMode == MouseMode.Build;

        if (!shouldShow) {
            if (firstEndpointGhostRoot != null) {
                Destroy(firstEndpointGhostRoot);
                firstEndpointGhostRoot = null;
                cachedFirstEndpointSt = null;
            }
            return;
        }

        // Rebuild on structType swap so the sprite tracks the current selection.
        if (firstEndpointGhostRoot != null && cachedFirstEndpointSt != st) {
            Destroy(firstEndpointGhostRoot);
            firstEndpointGhostRoot = null;
        }
        if (firstEndpointGhostRoot == null) {
            firstEndpointGhostRoot = new GameObject("firstEndpointGhost");
            firstEndpointGhostRoot.transform.SetParent(StructController.instance.transform, true);
            StructureVisualBuilder.Build(firstEndpointGhostRoot, st, st.GetShape(0),
                                         mirrored: false, rotation: 0, baseSortingOrder: 200,
                                         tint: new Color(0.8f, 0.9f, 1f, 0.5f));
            cachedFirstEndpointSt = st;
        }

        firstEndpointGhostRoot.transform.position = StructureVisuals.PositionFor(st, fe.x, fe.y);

        // Mirror by cursor position. When the cursor is exactly on the endpoint's
        // column, keep the last mirror — avoids flicker as the cursor crosses.
        if (cursorTile != null && cursorTile.x != fe.x) {
            bool mirrored = cursorTile.x > fe.x;
            var srs = firstEndpointGhostRoot.GetComponentsInChildren<SpriteRenderer>();
            for (int i = 0; i < srs.Length; i++) srs[i].flipX = mirrored;
        }
    }

    // Manages the translucent catenary preview between BuildPanel.firstEndpoint
    // and the currently-hovered tile, while a two-click placement is mid-flight.
    // Visually shows the player what curve their bridge would draw before they
    // commit the second click.
    //
    // Rebuilds only when (firstEndpoint, cursor tile, structType) changes — not
    // every frame the cursor sits on the same tile. Validity is NOT colour-
    // coded; CanPlaceTwoPoint already logs the rejection reason on click. Adding
    // green/red here would require running validation per cursor-move, doable
    // but skipped until asked.
    void UpdateGhostCatenary(Tile cursorTile) {
        BuildPanel bp = BuildPanel.instance;
        Tile fe = bp?.firstEndpoint;
        StructType st = bp?.structType;
        bool shouldShow = fe != null && cursorTile != null && cursorTile != fe
                          && st != null && st.placementMethod == "twoClick"
                          && mouseMode == MouseMode.Build;

        if (!shouldShow) {
            if (ghostCatenaryRoot != null) {
                Destroy(ghostCatenaryRoot);
                ghostCatenaryRoot = null;
                cachedGhostStart  = null;
                cachedGhostEnd    = null;
                cachedGhostSt     = null;
            }
            return;
        }

        // Skip rebuild when nothing changed.
        if (ghostCatenaryRoot != null
            && cachedGhostStart == fe
            && cachedGhostEnd   == cursorTile
            && cachedGhostSt    == st) return;

        if (ghostCatenaryRoot != null) Destroy(ghostCatenaryRoot);
        ghostCatenaryRoot = new GameObject("ghostCatenary");
        ghostCatenaryRoot.transform.SetParent(StructController.instance.transform, true);
        // sortingOrder 200 matches the cursor-following build preview tier, so
        // the catenary preview sorts above world content the same way.
        RopeBridge.BuildPreviewChain(ghostCatenaryRoot, st, fe.x, fe.y, cursorTile.x, cursorTile.y, 200);

        cachedGhostStart = fe;
        cachedGhostEnd   = cursorTile;
        cachedGhostSt    = st;
    }

    private void CommitHarvestDrag(Vector3 startScreen, Vector3 endScreen) {
        var (minX, maxX, minY, maxY) = GetDragWorldBounds(startScreen, endScreen);
        foreach (Plant p in PlantController.instance.Plants) {
            if (p.x >= minX && p.x <= maxX && p.y >= minY && p.y <= maxY)
                p.SetHarvestFlagged(true);
        }
    }

    // Converts a screen-space drag to world-space min/max bounds via the main camera.
    private (float minX, float maxX, float minY, float maxY) GetDragWorldBounds(Vector3 startScreen, Vector3 endScreen) {
        Vector3 worldA = Camera.main.ScreenToWorldPoint(startScreen); worldA.z = 0;
        Vector3 worldB = Camera.main.ScreenToWorldPoint(endScreen);   worldB.z = 0;
        return (Mathf.Min(worldA.x, worldB.x), Mathf.Max(worldA.x, worldB.x),
                Mathf.Min(worldA.y, worldB.y), Mathf.Max(worldA.y, worldB.y));
    }

    private void HandleSelectClick(Tile tileAt, Vector3 clickPos) {
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        // Any select-click cancels follow — the click either changes the selection
        // (removing the indicator on the followed mouse) or hits the same mouse
        // again, in which case the block below re-sets _followAnimal anyway.
        _followAnimal = null;

        // Shift+LMB on storage = copy filters
        Inventory clickStorage = GetStorageAt(tileAt);
        if (shift && clickStorage != null) {
            InventoryController.instance.CopyAllowed(clickStorage);
            return;
        }

        Collider2D[] hits = Physics2D.OverlapPointAll(clickPos);
        var animals = new System.Collections.Generic.List<Animal>();
        foreach (var col in hits) {
            Animal a = col.gameObject.GetComponent<Animal>();
            if (a != null) animals.Add(a);
        }

        // Click-to-follow: if any of the clicked animals is already in the active
        // InfoPanel selection, treat this as the "second click" and start tracking.
        // Checked before ShowSelection so the lookup uses the prior context.
        if (InfoPanel.instance != null) {
            foreach (var a in animals) {
                if (InfoPanel.instance.IsAnimalSelected(a)) { _followAnimal = a; break; }
            }
        }

        if (tileAt != null || animals.Count > 0) {
            var ctx = SelectionContext.FromTile(tileAt, animals);
            Inventory tileStorage = GetStorageAt(tileAt);
            if (ctrl && tileStorage != null) {
                // Ctrl+LMB: toggle this inventory in/out of the multi-selection
                InventoryController.instance.CtrlToggleInventory(tileStorage);
                Inventory primary = InventoryController.instance.selectedInventory;
                if (primary != null) {
                    Tile primaryTile = WorldController.instance.world.GetTileAt(primary.x, primary.y);
                    InfoPanel.instance.ShowSelection(SelectionContext.FromTile(primaryTile));
                }
            } else {
                InfoPanel.instance.ShowSelection(ctx);
                if (animals.Count > 0)
                    InventoryController.instance.SelectInventory(null);
                else if (tileStorage != null)
                    InventoryController.instance.SelectInventory(tileStorage);
                else
                    InventoryController.instance.SelectInventory(null);
            }
        }
    }

    // Selects all storage inventories whose tile falls inside the screen-space drag rectangle.
    private void CommitDragSelect(Vector3 startScreen, Vector3 endScreen) {
        // Drag-select picks new things — always cancel follow.
        _followAnimal = null;
        var (minX, maxX, minY, maxY) = GetDragWorldBounds(startScreen, endScreen);
        var found = new System.Collections.Generic.List<Inventory>();
        Inventory primary = null;
        foreach (Inventory inv in InventoryController.instance.inventories) {
            if (!IsStorageType(inv.invType)) continue;
            if (inv.x >= minX && inv.x <= maxX && inv.y >= minY && inv.y <= maxY) {
                found.Add(inv);
                primary = inv;
            }
        }

        if (found.Count == 0) {
            InventoryController.instance.SelectInventory(null);
            InfoPanel.instance.ShowInfo((Tile)null);
            return;
        }

        Tile primaryTile = WorldController.instance.world.GetTileAt(primary.x, primary.y);
        if (primaryTile != null) InfoPanel.instance.ShowInfo(primaryTile);
        InventoryController.instance.SelectInventories(found, primary);
    }

    // Positions the drag-rect UI image between the two screen-space corners.
    //
    // Convert screen pixels → parent's local UI units before assigning. The parent canvas
    // uses CanvasScaler "Scale With Screen Size", so sizeDelta is in reference units, not
    // screen pixels — feeding it raw mouse deltas inflates the rect by the canvas scale and
    // makes the corners walk away from the start point as the drag grows. anchoredPosition
    // and sizeDelta share the same local units, so the rect renders correctly at any res.
    private void UpdateDragRect(Vector3 startScreen, Vector3 currentScreen) {
        if (dragRectTransform == null) return;
        RectTransform parentRT = dragRectTransform.parent as RectTransform;
        if (parentRT == null) return;
        Canvas canvas = dragRectTransform.GetComponentInParent<Canvas>();
        // Camera arg must be null for ScreenSpaceOverlay; for Camera/World canvases we pass
        // the canvas's worldCamera so the conversion accounts for the projected canvas plane.
        Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
        Vector2 startLocal, endLocal;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRT, startScreen, cam, out startLocal);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRT, currentScreen, cam, out endLocal);
        dragRectTransform.anchoredPosition = (startLocal + endLocal) * 0.5f;
        dragRectTransform.sizeDelta = new Vector2(
            Mathf.Abs(endLocal.x - startLocal.x),
            Mathf.Abs(endLocal.y - startLocal.y));
    }

    public void SetModeBuild() {
        mouseMode = MouseMode.Build;
    }
    public void SetModeRemove() {
        mouseMode = MouseMode.Remove;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.firstEndpoint = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }
    public void SetModeHarvest() {
        mouseMode = MouseMode.Harvest;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.firstEndpoint = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }
    public void SetModeSelect() {
        mouseMode = MouseMode.Select;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.firstEndpoint = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }

    static bool IsStorageType(Inventory.InvType t) =>
        t == Inventory.InvType.Storage || t == Inventory.InvType.Market;

    // Returns the storage/market inventory at a tile via the building, or null.
    static Inventory GetStorageAt(Tile t) {
        Inventory s = t?.building?.storage;
        return (s != null && IsStorageType(s.invType)) ? s : null;
    }

    // ── Zoom / camera API ──────────────────────────────────────────────────
    // Small public surface so other systems (e.g. SaveSystem) can query and
    // restore zoom without duplicating the assetsPPU reflection dance.

    // Current PPU, or null if the PixelPerfectCamera wasn't located on Camera.main.
    public int? GetZoomPPU() {
        if (ppcAssetsPPU == null || ppcComponent == null) return null;
        return (int)ppcAssetsPPU.GetValue(ppcComponent);
    }

    // Sets PPU and re-clamps the camera. Uses the same estimated half-height
    // trick as the scroll-wheel path because PPC updates orthographicSize in
    // its own LateUpdate, one frame after we change assetsPPU.
    public void SetZoomPPU(int newPPU) {
        if (ppcAssetsPPU == null || ppcComponent == null) {
            Debug.LogError("SetZoomPPU: PixelPerfectCamera not found on Camera.main."); return;
        }
        int currentPPU = (int)ppcAssetsPPU.GetValue(ppcComponent);
        if (currentPPU == newPPU) { ClampCamera(); return; }
        ppcAssetsPPU.SetValue(ppcComponent, newPPU);
        float estimatedHalfH = Camera.main.orthographicSize * currentPPU / newPPU;
        if (world == null) world = WorldController.instance?.world;
        ClampCameraToWorld(estimatedHalfH);
    }

    // ── Camera pan (WASD + screen edge) ────────────────────────────────────
    // Sums keyboard and edge-of-screen inputs into a single normalized direction,
    // then translates the camera at panSpeed (world tiles / sec) in unscaled time
    // so panning still works while the game is paused.
    //
    // Skipped entirely while RMB-drag is active — that's the precise pan, and
    // these secondary inputs shouldn't fight it. WASD also yields when a text
    // input is focused so trade-price / save-name fields aren't hijacked.
    private void HandleCameraPan() {
        if (Camera.main == null) return;

        Vector2 dir = Vector2.zero;
        bool rmbDrag = Input.GetMouseButton(1);

        // Collect inputs unless RMB-drag is active — when it is, dir stays zero and
        // the velocity decays so any in-progress coast eases out instead of snapping.
        if (!rmbDrag) {
            if (!IsInputFieldFocused()) {
                if (Input.GetKey(KeyCode.W)) dir.y += 1f;
                if (Input.GetKey(KeyCode.S)) dir.y -= 1f;
                if (Input.GetKey(KeyCode.D)) dir.x += 1f;
                if (Input.GetKey(KeyCode.A)) dir.x -= 1f;
            }

            // Edge-pan: cursor must be in-window, not over UI, and the app must
            // have focus — otherwise an alt-tabbed user's offscreen cursor would
            // silently drift the camera in the background.
            if (edgePanEnabled && Application.isFocused) {
                Vector3 mp = Input.mousePosition;
                bool inWindow = mp.x >= 0f && mp.x < Screen.width && mp.y >= 0f && mp.y < Screen.height;
                bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                if (inWindow && !overUI) {
                    if (mp.x < edgePanInsetPx) dir.x -= 1f;
                    else if (mp.x >= Screen.width - edgePanInsetPx) dir.x += 1f;
                    if (mp.y < edgePanInsetPx) dir.y -= 1f;
                    else if (mp.y >= Screen.height - edgePanInsetPx) dir.y += 1f;
                }
            }
        }

        // Any pan input (keys, edge, or RMB drag) cancels follow — user is taking
        // manual control. Checked here so double-click → immediate WASD release works.
        bool hasPanInput = dir.sqrMagnitude > 0.0001f || rmbDrag;
        if (hasPanInput) _followAnimal = null;

        // Normalize so diagonals aren't √2× faster than cardinals. Then move the
        // current velocity toward the input target at a rate that crosses the full
        // speed range in panRampTime seconds — same ramp for spin-up and wind-down.
        Vector2 target = (dir.sqrMagnitude > 0.0001f) ? dir.normalized * panSpeedScreens : Vector2.zero;
        float maxStep = (panRampTime > 0f) ? (panSpeedScreens / panRampTime) * Time.unscaledDeltaTime : float.MaxValue;
        _panVelocity = Vector2.MoveTowards(_panVelocity, target, maxStep);

        float orthoH = Camera.main.orthographicSize * 2f;
        if (_panVelocity.sqrMagnitude > 1e-8f) {
            Vector2 worldStep = _panVelocity * orthoH * Time.unscaledDeltaTime;
            Camera.main.transform.Translate(worldStep.x, worldStep.y, 0f);
            if (world == null) world = WorldController.instance?.world;
            ClampCameraToWorld();
        }

    }

    // Follow snap runs here (not in Update) so we read the animal's freshly-
    // updated position for the frame — see the field comment for the rationale.
    // Unity's overloaded == nulls the ref when the GameObject is destroyed,
    // so this also handles the followed animal dying.
    void LateUpdate() {
        if (_followAnimal == null || Camera.main == null) return;
        Vector3 camPos = Camera.main.transform.position;
        camPos.x = _followAnimal.x;
        camPos.y = _followAnimal.y;
        Camera.main.transform.position = camPos;
        if (world == null) world = WorldController.instance?.world;
        ClampCameraToWorld();
    }

    // Keyboard pan must yield to text entry — checks both TMP and legacy InputField.
    private static bool IsInputFieldFocused() {
        var sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (sel == null) return false;
        if (sel.GetComponent<TMPro.TMP_InputField>() != null) return true;
        if (sel.GetComponent<InputField>() != null) return true;
        return false;
    }

    // Public wrapper so callers outside MouseController can re-clamp (e.g.
    // after a save-load restores camera position).
    public void ClampCamera() {
        if (world == null) world = WorldController.instance?.world;
        ClampCameraToWorld();
    }

    // Centres the camera on a world point once, without engaging follow.
    // Used by UI jumps like clicking a job name to look at that mouse —
    // clears any active follow so the jump sticks instead of snapping back.
    public void CenterCameraOn(float x, float y) {
        if (Camera.main == null) return;
        _followAnimal = null;
        Vector3 pos = Camera.main.transform.position;
        pos.x = x;
        pos.y = y;
        Camera.main.transform.position = pos;
        if (world == null) world = WorldController.instance?.world;
        ClampCameraToWorld();
    }

    // ── Camera bounds ──────────────────────────────────────────────────────
    // Clamps the camera so the viewport never shows outside the world rectangle.
    // Tiles occupy x ∈ [0, nx-1] and y ∈ [0, ny-1] (centered on integers),
    // so the world edge is 0.5 tiles beyond the outermost tile centres.
    // ppu: pass explicitly after a zoom change because PPC updates orthographicSize
    // one frame later; omit (or pass 0) to read the current PPC value.
    // estimatedHalfH: pass after a zoom change (PPC hasn't updated orthographicSize yet).
    // Omit (or pass 0) during pan — orthographicSize is already current.
    void ClampCameraToWorld(float estimatedHalfH = 0f) {
        if (world == null) return;

        float halfH = estimatedHalfH > 0f ? estimatedHalfH : Camera.main.orthographicSize;
        float halfW = halfH * Camera.main.aspect;

        float worldW = world.nx; // tile centres at 0..nx-1, edges at -0.5..nx-0.5
        float worldH = world.ny;

        // Stop when the world edge aligns with the viewport edge.
        float minX = halfW - 0.5f;
        float maxX = worldW - 0.5f - halfW;
        float minY = halfH - 0.5f;
        float maxY = worldH - 0.5f - halfH;

        Vector3 pos = Camera.main.transform.position;
        // If the viewport is wider/taller than the world, centre on that axis.
        pos.x = (minX > maxX) ? (worldW - 1f) / 2f : Mathf.Clamp(pos.x, minX, maxX);
        pos.y = (minY > maxY) ? (worldH - 1f) / 2f : Mathf.Clamp(pos.y, minY, maxY);
        Camera.main.transform.position = pos;
    }
}
