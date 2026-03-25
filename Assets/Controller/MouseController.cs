using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Reflection;

// note: this is not really a controller. it just manages mouse input

public class MouseController : MonoBehaviour {
    public enum MouseMode {Select, Build, Remove};
    public MouseMode mouseMode = MouseMode.Select;
    public GameObject buildPreview;
    SpriteRenderer buildPreviewSr;
    Sprite buildPreviewDefaultSprite;
    Vector3 prevPosition;

    public World world;
    public static MouseController instance { get; protected set; }
    Component ppcComponent;
    PropertyInfo ppcAssetsPPU;

    // --- drag-select state ---
    private Vector3 _dragStartScreenPos;
    private bool _isDragging = false;
    private const float DragThresholdPixels = 8f;
    [SerializeField] private RectTransform dragRectTransform; // assign in inspector (Screen Space Overlay Image)

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one mouse controller");}
        instance = this;
        buildPreviewSr = buildPreview.GetComponent<SpriteRenderer>();
        buildPreviewSr.sortingOrder = 200;
        buildPreviewDefaultSprite = buildPreviewSr.sprite;
        foreach (var c in Camera.main.GetComponents<Component>()) {
            var prop = c.GetType().GetProperty("assetsPPU");
            if (prop != null) { ppcComponent = c; ppcAssetsPPU = prop; break; }
        }
    }

    void Update() {
        if (Input.GetKeyDown(KeyCode.Escape) && mouseMode != MouseMode.Select)
            SetModeSelect();
        if (Input.GetKeyDown(KeyCode.D) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) {
            WorkOrderManager.instance?.AuditOrders();
            InventoryController.instance?.ValidateGlobalInventory();
        }

        if (EventSystem.current.IsPointerOverGameObject()){
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
            int current = (int)ppcAssetsPPU.GetValue(ppcComponent);
            int idx = System.Array.IndexOf(zoomLevels, current);
            if (idx == -1) idx = System.Array.BinarySearch(zoomLevels, current);
            if (idx < 0) idx = ~idx;
            idx = Mathf.Clamp(idx + (scroll > 0 ? 1 : -1), 0, zoomLevels.Length - 1);
            ppcAssetsPPU.SetValue(ppcComponent, zoomLevels[idx]);
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
        else if ((mouseMode == MouseMode.Build) || (mouseMode == MouseMode.Remove)){
            buildPreview.SetActive(true);
            if (mouseMode == MouseMode.Build && st != null && anchorTile != null) {
                Sprite buildSprite = st.LoadSprite();
                buildPreviewSr.sprite = buildSprite != null ? buildSprite : buildPreviewDefaultSprite;
                buildPreviewSr.color = buildSprite != null ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
                buildPreview.transform.localScale = buildSprite == null
                    ? new Vector3(st.nx, Mathf.Max(1, st.ny), 1f)
                    : Vector3.one;
                float visualX = anchorTile.x + (st.nx - 1) / 2.0f;
                buildPreview.transform.position = new Vector3(visualX, anchorTile.y, -1);
            } else {
                buildPreview.transform.position = new Vector3(tileAt.x, tileAt.y, -1);
                buildPreviewSr.sprite = buildPreviewDefaultSprite;
                buildPreviewSr.color = Color.white;
                buildPreview.transform.localScale = Vector3.one;
            }
        }
        if (mouseMode == MouseMode.Select){
            buildPreview.SetActive(false);
        }


        // Shift+RMB on storage = paste filters (before drag handling consumes the click)
        if (Input.GetMouseButtonDown(1) && mouseMode == MouseMode.Select
            && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            && tileAt?.inv != null && IsStorageType(tileAt.inv.invType)) {
            InventoryController.instance.PasteAllowed(tileAt.inv);
        }

        // LMB down: record drag start for Select mode; immediate action for Build/Remove
        if (Input.GetMouseButtonDown(0)) {
            if (mouseMode == MouseMode.Select) {
                _dragStartScreenPos = Input.mousePosition;
                _isDragging = false;
            } else if (mouseMode == MouseMode.Build) {
                Tile placeTile = anchorTile ?? tileAt;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (BuildPanel.instance.PlaceBlueprint(placeTile) && !shift)
                    mouseMode = MouseMode.Select;
            } else if (mouseMode == MouseMode.Remove) {
                BuildPanel.instance.Remove(tileAt);
            }
        }

        // LMB held in Select mode: check drag threshold and update visual rect
        if (Input.GetMouseButton(0) && mouseMode == MouseMode.Select) {
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
        }
    }

    private void HandleSelectClick(Tile tileAt, Vector3 clickPos) {
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        // Shift+LMB on storage = copy filters
        if (shift && tileAt?.inv != null && IsStorageType(tileAt.inv.invType)) {
            InventoryController.instance.CopyAllowed(tileAt.inv);
            return;
        }

        Collider2D[] hits = Physics2D.OverlapPointAll(clickPos);
        var animals = new System.Collections.Generic.List<Animal>();
        foreach (var col in hits) {
            Animal a = col.gameObject.GetComponent<Animal>();
            if (a != null) animals.Add(a);
        }

        if (animals.Count > 0) {
            InfoPanel.instance.ShowInfo(animals);
            InventoryController.instance.SelectInventory(null);
        } else if (tileAt != null) {
            bool hasStorageInv = tileAt.inv != null && IsStorageType(tileAt.inv.invType);
            if (ctrl && hasStorageInv) {
                // Ctrl+LMB: toggle this inventory in/out of the multi-selection
                InventoryController.instance.CtrlToggleInventory(tileAt.inv);
                Inventory primary = InventoryController.instance.selectedInventory;
                if (primary != null) {
                    Tile primaryTile = WorldController.instance.world.GetTileAt(primary.x, primary.y);
                    InfoPanel.instance.ShowInfo(primaryTile);
                }
            } else {
                InfoPanel.instance.ShowInfo(tileAt);
                if (hasStorageInv)
                    InventoryController.instance.SelectInventory(tileAt.inv);
                else
                    InventoryController.instance.SelectInventory(null);
            }
        }
    }

    /// <summary>Selects all storage inventories whose tile falls inside the screen-space drag rectangle.</summary>
    private void CommitDragSelect(Vector3 startScreen, Vector3 endScreen) {
        Vector3 worldA = Camera.main.ScreenToWorldPoint(startScreen); worldA.z = 0;
        Vector3 worldB = Camera.main.ScreenToWorldPoint(endScreen);   worldB.z = 0;
        float minX = Mathf.Min(worldA.x, worldB.x), maxX = Mathf.Max(worldA.x, worldB.x);
        float minY = Mathf.Min(worldA.y, worldB.y), maxY = Mathf.Max(worldA.y, worldB.y);

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

    /// <summary>Positions the drag-rect UI image between the two screen-space corners.</summary>
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
    public void SetModeSelect() {
        mouseMode = MouseMode.Select;
        if (BuildPanel.instance != null){
            BuildPanel.instance.structType = null;
            BuildPanel.instance.CloseSubPanel();
        }
    }

    static bool IsStorageType(Inventory.InvType t) =>
        t == Inventory.InvType.Storage || t == Inventory.InvType.Market || t == Inventory.InvType.Liquid;
}
