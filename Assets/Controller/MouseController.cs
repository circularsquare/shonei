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
    // Per-tile child SRs spawned for shape-aware vertical previews (`_m` middles, `_t` top).
    // Pooled across builds — never released, just SetActive(false) when not needed.
    List<SpriteRenderer> previewExtensionSrs = new List<SpriteRenderer>();
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

    // --- debug cursor light (Ctrl+T toggles SetActive) ---
    // Wire a GameObject with a LightSource (and optional sortOrderOverride) in the
    // inspector. Keep it disabled by default so it only appears when toggled on.
    [SerializeField] private GameObject debugCursorLight;

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one mouse controller");}
        instance = this;
        buildPreviewSr = buildPreview.GetComponent<SpriteRenderer>();
        buildPreviewSr.sortingOrder = 200;
        LightReceiverUtil.SetSortBucket(buildPreviewSr);
        buildPreviewDefaultSprite = buildPreviewSr.sprite;
        foreach (var c in Camera.main.GetComponents<Component>()) {
            var prop = c.GetType().GetProperty("assetsPPU");
            if (prop != null) { ppcComponent = c; ppcAssetsPPU = prop; break; }
        }
    }

    void Update() {
        if (Input.GetKeyDown(KeyCode.Escape) && mouseMode != MouseMode.Select)
            SetModeSelect();
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
        if (Input.GetKeyDown(KeyCode.D) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) {
            WorkOrderManager.instance?.AuditOrders();
            InventoryController.instance?.ValidateGlobalInventory();
            var ws = WeatherSystem.instance;
            if (ws != null)
                Debug.Log($"Temperature: {ws.temperature:F1}°C, Season: {ws.GetSeason()} (day {ws.GetDayOfYear():F1})");
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

        bool overUI = EventSystem.current.IsPointerOverGameObject();
        if (overUI && !_isDragging) {
            if (Input.GetMouseButtonDown(0) && mouseMode == MouseMode.Build)
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
            ppcAssetsPPU.SetValue(ppcComponent, newPPU);
            // PPC updates orthographicSize in its own LateUpdate, so estimate the new
            // half-height by scaling the current size by the PPU ratio (exact if zoom factor is stable).
            float estimatedHalfH = Camera.main.orthographicSize * currentPPU / newPPU;
            ClampCameraToWorld(estimatedHalfH);
        }

        // draggin world around
        Vector3 currPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        currPosition.z = 1f;
        if (Input.GetMouseButtonDown(1)){
            prevPosition = Input.mousePosition;
            // if (mouseMode == MouseMode.Build) SetModeSelect();             // cancels build mode on right click
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
                bool shapeAware = st.HasShapes;
                Shape shape = st.GetShape(shapeIndex);
                Sprite anchorSprite = shapeAware
                    ? StructureVisuals.LoadShapeSprite(st, shape, 0)
                    : st.LoadSprite();
                buildPreviewSr.sprite = anchorSprite != null ? anchorSprite : buildPreviewDefaultSprite;
                buildPreviewSr.flipX = BuildPanel.instance != null && BuildPanel.instance.mirrored;
                buildPreviewSr.color = anchorSprite != null ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
                if (anchorSprite == null) {
                    buildPreviewSr.drawMode = SpriteDrawMode.Sliced;
                    buildPreviewSr.size = new Vector2(st.nx, Mathf.Max(1, st.ny));
                } else {
                    buildPreviewSr.drawMode = SpriteDrawMode.Simple;
                }
                buildPreview.transform.localScale = Vector3.one;
                // Shape-aware previews anchor at the bottom-left tile so per-tile child SRs
                // line up. Legacy multi-tile uses centred positioning.
                buildPreview.transform.position = shapeAware
                    ? new Vector3(anchorTile.x, anchorTile.y + (st.depth == 3 ? -1f/8f : 0f), -1f)
                    : StructureVisuals.PositionFor(st, anchorTile.x, anchorTile.y, z: -1);
                // Rotation preview — only meaningful when the type is rotatable; BuildPanel.rotation
                // is reset on SetStructType so we won't carry stale rotation across builds.
                int rot = (BuildPanel.instance != null) ? BuildPanel.instance.rotation : 0;
                buildPreview.transform.rotation = StructureVisuals.RotationFor(rot);
                // Compose the shape-aware preview from per-tile sprites for vertical extension.
                SyncPreviewExtensions(st, shape, shapeAware);
            } else {
                buildPreview.transform.position = new Vector3(tileAt.x, tileAt.y, -1);
                buildPreviewSr.sprite = buildPreviewDefaultSprite;
                buildPreviewSr.flipX = false;
                buildPreviewSr.color = Color.white;
                buildPreviewSr.drawMode = SpriteDrawMode.Simple;
                buildPreview.transform.localScale = Vector3.one;
                buildPreview.transform.rotation = Quaternion.identity;
                // Hide all extension preview SRs when not in shape-aware build mode.
                SyncPreviewExtensions(null, null, false);
            }
        }
        if (mouseMode == MouseMode.Select){
            buildPreview.SetActive(false);
        }


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
                Tile placeTile = anchorTile ?? tileAt;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (BuildPanel.instance.PlaceBlueprint(placeTile) && !shift)
                    mouseMode = MouseMode.Select;
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

    // Mirrors Structure / Blueprint's per-tile sprite stack onto the build-mode preview
    // ghost so the cursor-following visual matches what will actually be placed (e.g. a
    // height-3 platform appears as `_b` + `_m` + `_t`, not a single sliced sprite).
    // Pools child SRs across calls — never destroys, just SetActive toggles. Pass
    // shapeAware=false / null args to deactivate all extension SRs.
    private void SyncPreviewExtensions(StructType st, Shape shape, bool shapeAware) {
        // Determine how many extension tiles we need this frame.
        int needed = 0;
        if (shapeAware && st != null && shape != null && shape.nx == 1 && shape.ny > 1)
            needed = shape.ny - 1;

        // Grow the pool to cover `needed`.
        while (previewExtensionSrs.Count < needed) {
            int dy = previewExtensionSrs.Count + 1;
            GameObject extGo = new GameObject($"buildPreviewExt{dy}");
            extGo.transform.SetParent(buildPreview.transform, false);
            SpriteRenderer extSr = extGo.AddComponent<SpriteRenderer>();
            extSr.sortingOrder = buildPreviewSr.sortingOrder;
            LightReceiverUtil.SetSortBucket(extSr);
            previewExtensionSrs.Add(extSr);
        }

        // Activate/configure the first `needed`, deactivate the rest.
        for (int i = 0; i < previewExtensionSrs.Count; i++) {
            SpriteRenderer extSr = previewExtensionSrs[i];
            if (i < needed) {
                int dy = i + 1;
                extSr.gameObject.SetActive(true);
                extSr.transform.localPosition = new Vector3(0f, dy, 0f);
                extSr.sprite = StructureVisuals.LoadShapeSprite(st, shape, dy);
                extSr.color = buildPreviewSr.color;
                extSr.flipX = buildPreviewSr.flipX;
            } else if (extSr.gameObject.activeSelf) {
                extSr.gameObject.SetActive(false);
            }
        }
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
    private void UpdateDragRect(Vector3 startScreen, Vector3 currentScreen) {
        if (dragRectTransform == null) return;
        // Screen Space Overlay: RectTransform.position is in screen pixels, same origin as Input.mousePosition
        dragRectTransform.position = new Vector3(
            (startScreen.x + currentScreen.x) / 2f,
            (startScreen.y + currentScreen.y) / 2f, 0f);
        dragRectTransform.sizeDelta = new Vector2(
            Mathf.Abs(currentScreen.x - startScreen.x),
            Mathf.Abs(currentScreen.y - startScreen.y));
    }

    public void SetModeBuild() {
        mouseMode = MouseMode.Build;
    }
    public void SetModeRemove() {
        mouseMode = MouseMode.Remove;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }
    public void SetModeHarvest() {
        mouseMode = MouseMode.Harvest;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }
    public void SetModeSelect() {
        mouseMode = MouseMode.Select;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
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

    // Public wrapper so callers outside MouseController can re-clamp (e.g.
    // after a save-load restores camera position).
    public void ClampCamera() {
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
