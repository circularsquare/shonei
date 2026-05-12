using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;

// A bag of ItemStacks. One inventory per floor tile / storage building / animal slot /
// blueprint / market / reservoir; type drives decay rate, allow-list behavior, and
// rendering. Public API is Produce (adds + updates GlobalInventory) and MoveItemTo
// (transfers, no double-count); AddItem is private — never call it externally.
public class Inventory{
    public int nStacks;
    public int stackSize; 
    public ItemStack[] itemStacks;
    public enum InvType {Floor, Storage, Animal, Market, Equip, Blueprint, Reservoir, Furnishing};

    // Returns true if this item type is physically compatible with this inventory.
    // This is a hard constraint checked before the per-item allowed[] dict.
    // Storage inventories always enforce class match (Default↔Default solid goods, Liquid↔Liquid
    // tanks, Book↔Book bookshelves). Non-storage inventories normally accept anything, but can
    // opt in to a class restriction by being constructed with a non-Default storageClass —
    // e.g. an Animal's bookSlotInv (Equip type, storageClass=Book) accepts only books.
    public bool ItemTypeCompatible(Item item) {
        if (invType != InvType.Storage && storageClass == ItemClass.Default) return true;
        return item.itemClass == storageClass;
    }
    public InvType invType;
    // Item class this storage accepts (derived from StructType.storageClass at construction).
    // Per-item allowed[] filter starts all-false for Storage — the player opts in via the filter UI.
    // Not saved; rebuilt from StructType on load.
    public ItemClass storageClass { get; private set; }
    public bool isLiquidStorage => storageClass == ItemClass.Liquid; // convenience for tank-specific rendering code
    public int x, y;
    public Dictionary<int, bool> allowed;

    // Per-stack item filter, parallel to itemStacks. When non-null, AddItem refuses to put an
    // item into stack[i] unless MatchesItem(item, slotConstraints[i]) — used by Blueprint to
    // bind each cost slot (sized for that cost's quantity) to the item it expects, so that
    // delivery order can't trap a small-quantity item in a stack sized for a different cost.
    // Constraints accept group items (e.g. "stone"), matching any leaf descendant (limestone,
    // granite, slate, gypsum). Null on inventories that don't need slot routing.
    // Negative-quantity AddItem (subtraction) ignores this — items already in a slot can
    // always be removed regardless of the slot's intended item.
    public Item[] slotConstraints;
    public bool locked = false; // when true, no items accepted and all existing items are treated as needing haul-out
    public string displayName;
    // Set true at the top of Destroy(). Any mutation/render op on a destroyed inv is a stale-reference
    // bug (animal still holds a cached Inventory ref after the inv was torn down). AddItem / MoveItemTo /
    // UpdateSprite check this and LogError-no-op instead of NREing on the nulled-out `go`.
    public bool destroyed { get; private set; }
    public GameObject go;
    private GameObject[] stackGos; // per-stack sprites for multi-stack storage (e.g. drawer)

    private static readonly Vector2[] quarterOffsets = {
        new Vector2(-0.25f,  0.25f), // top-left
        new Vector2( 0.25f,  0.25f), // top-right
        new Vector2(-0.25f, -0.25f), // bottom-left
        new Vector2( 0.25f, -0.25f), // bottom-right
    };

    public GlobalInventory ginv;

    // target quantity per item for the market; merchants aim to keep inventory at these levels.
    // null on non-market inventories.
    public Dictionary<Item, int> targets;

    // World.timer value when the player last manually edited a market target.
    // HaulToMarket orders are suppressed for 3 reconcile ticks (30s) after a change
    // so targets can settle before merchants are dispatched.
    // NegativeInfinity = never updated, so the guard is always inactive at startup/load.
    public float lastTargetManualUpdateTimer = float.NegativeInfinity;

    // World.timer threshold past which this inventory is dry. Set by MarkWet from
    // WeatherSystem when rain is falling on an exposed floor inventory; while
    // World.instance.timer < wetUntil, Decay() doubles the per-tick decay multiplier.
    // Only meaningful for InvType.Floor — other types ignore it. Persisted in saves.
    public float wetUntil = 0f;

    // Available space for an item in the market inventory (respects in-flight delivery reservations).
    // Separate from GetStorageForItem, which intentionally excludes market to prevent normal haulers routing here.
    public int GetMarketSpace(Item item) {
        int space = 0;
        foreach (ItemStack stack in itemStacks)
            space += stack.FreeSpace(item);
        return space;
    }

    // parentSortingOrder: for Storage inventories, the sortingOrder of the owning building's
    // sprite. Storage stack/placeholder sprites render at parentSortingOrder + 1 so they sit
    // just above the building they belong to (e.g. drawer at 10 → stacks at 11). Pass -1
    // (the default) to fall back to the legacy hardcoded 30 — used by tests that don't
    // construct a real building.
    public Inventory(int n = 1, int stackSize = 2500, InvType invType = InvType.Floor, int x = 0, int y = 0, ItemClass storageClass = ItemClass.Default, int parentSortingOrder = -1) {
        nStacks = n;
        this.stackSize = stackSize;
        this.invType = invType;
        this.x = x;
        this.y = y;
        this.storageClass = storageClass;
        displayName = invType.ToString().ToLower();
        itemStacks = new ItemStack[nStacks];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(this, null, 0, stackSize);
        }

        // Storage sprites sit one sortingOrder above their parent building so they read
        // as "stuff on the building" rather than ambiguous mid-air items.
        int storageOrder = parentSortingOrder >= 0 ? parentSortingOrder + 1 : 30;

        if (invType == InvType.Storage) {
            allowed = Db.itemsFlat.ToDictionary(i => i.id, i => false); // all disallowed by default for dry storage & tanks; user enables per-item via the filter UI
            // Bookshelves auto-allow every book — players rarely care which book goes where, and the
            // shelf is class-locked anyway so nothing else can land in it. Tanks deliberately do NOT
            // auto-allow liquids: a tank is usually dedicated to one liquid (e.g. all water, no
            // soymilk), so the manual opt-in is a feature there.
            if (storageClass == ItemClass.Book) {
                foreach (Item it in Db.itemsFlat)
                    if (it.itemClass == ItemClass.Book) allowed[it.id] = true;
            }
        } else {
            allowed = Db.itemsFlat.ToDictionary(i => i.id, i => true);
        }
        

        // Bookshelves (storageClass=Book) use single-sprite rendering regardless of nStacks:
        // the whole shelf shows one of three fill-level sprites (slow/smid/shigh). Exclude from
        // the per-slot multi-stack path below.
        bool useMultiStackRendering = invType == InvType.Storage && nStacks > 1 && storageClass != ItemClass.Book;
        if (useMultiStackRendering){
            // Multi-stack storage (drawer): one sprite per stack slot in a 2x2 grid
            stackGos = new GameObject[nStacks];
            for (int i = 0; i < nStacks && i < quarterOffsets.Length; i++){
                stackGos[i] = new GameObject("InventoryStack_" + i);
                stackGos[i].transform.position = new Vector3(x + quarterOffsets[i].x, y + quarterOffsets[i].y, 0);
                stackGos[i].transform.SetParent(InventoryController.instance.transform, true);
                SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(stackGos[i]);
                sr.sortingOrder = storageOrder;
                LightReceiverUtil.SetSortBucket(sr);
            }
        } else if (invType == InvType.Floor || invType == InvType.Storage){
            go = new GameObject();
            go.transform.position = new Vector3(x, y, 0);
            go.transform.SetParent(InventoryController.instance.transform, true);
            SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(go);
            if (invType == InvType.Floor) { sr.sortingOrder = ComputeFloorSortingOrder(); }
            else { sr.sortingOrder = storageOrder; }
            LightReceiverUtil.SetSortBucket(sr);
            // Bookshelves render their own fill sprite via UpdateSprite — skip the generic Storage placeholder.
            if (storageClass != ItemClass.Book) {
                string spriteName = (invType == InvType.Storage && isLiquidStorage) ? "Liquid" : invType.ToString();
                sr.sprite = Resources.Load<Sprite>("Sprites/Inventory/" + spriteName);
            }
        }

        if (invType == InvType.Market) {
            targets = Db.itemsFlat.ToDictionary(i => i, i => 0);
        }

        InventoryController.instance.AddInventory(this);
        ginv = GlobalInventory.instance;
    }

    // Compacts top N frames of System.Environment.StackTrace into "Class.Method (File.cs:Line)"
    // joined by " <- ", starting after the frame containing `anchor`. Falls back to the raw
    // line if the Mono format doesn't match (e.g. IL2CPP).
    private static readonly System.Text.RegularExpressions.Regex _stackFrameRx =
        new(@"^\s*at\s+(.+?)\s+\([^)]*\)\s+\[[^\]]+\]\s+in\s+.*[/\\]([^/\\]+):(\d+)\s*$");
    private static string FormatCallerTrace(string anchor, int frames = 4){
        string[] lines = System.Environment.StackTrace.Split('\n');
        int startIdx = Array.FindIndex(lines, l => l.Contains(anchor));
        int from = startIdx >= 0 ? startIdx + 1 : 0;
        int to = Math.Min(from + frames, lines.Length);
        var parts = new List<string>(frames);
        for (int i = from; i < to; i++){
            string line = lines[i].Replace("\r", "").Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var m = _stackFrameRx.Match(line);
            parts.Add(m.Success ? $"{m.Groups[1].Value} ({m.Groups[2].Value}:{m.Groups[3].Value})" : line);
        }
        return string.Join(" <- ", parts);
    }

    // `except` is the task whose own action triggered this destroy (e.g. a FetchObjective
    // that just emptied a floor and is calling Destroy as a cleanup step). That task is mid-
    // success and handles its own follow-up — failing it here would clobber a chained task
    // (Fetch → Craft → Drop) right after Fetch succeeded. Pass null for "no originator".
    // `reason` describes the cause for the abort log (e.g. "Vesper fetched 80/100 apple",
    // "items fell to (3,4)", "building deconstructed"). Surfaces in the per-task log so we
    // can tell at a glance whether reservations are getting clobbered by the expected source
    // (a fetch finishing the stack) or something less obvious (decay, fall, structural change).
    public void Destroy(Task except = null, string reason = null){
        destroyed = true;
        // Eagerly remove haul orders for floor and storage stacks, then zero quantities as a safety net
        // for PruneStaleHauls (covers other inv types and any orders that slip through).
        if (invType == InvType.Floor || invType == InvType.Storage)
            foreach (ItemStack stack in itemStacks)
                WorkOrderManager.instance?.RemoveHaulForStack(stack);
        // Notify reservers: any task with a live source/space reservation on this inv aborts now,
        // so the animal re-plans immediately instead of walking to a now-dead source. Without this,
        // the FetchObjective wouldn't notice the destruction until it arrived (see "destroyed before
        // arrival" in FetchObjective.OnArrival), wasting a full walk.
        // Skipped during ClearWorld: bulk teardown tears everything down together, so dangling
        // reservations are expected and not a bug.
        if (!WorldController.isClearing && AnimalController.instance != null) {
            var ac = AnimalController.instance;
            string causeStr = string.IsNullOrEmpty(reason) ? "" : $" — {reason}";
            for (int i = 0; i < ac.na; i++) {
                Animal a = ac.animals[i];
                Task t = a?.task;
                if (t == null || t == except) continue;
                if (!t.HasReservationOn(this)) continue;
                // TODO: temporary log while we observe how often this fires in practice.
                Debug.Log($"Inventory.Destroy: {invType} '{displayName}' at ({x},{y}){causeStr} — aborting {a.aName}'s {t} task.");
                t.Fail(silent: true); // we just logged a more specific message
            }
        }
        foreach (ItemStack stack in itemStacks) { stack.quantity = 0; stack.resAmount = 0; stack.resSpace = 0; stack.resSpaceItem = null; }
        if (go != null){GameObject.Destroy(go); go = null;}
        if (stackGos != null){
            foreach (GameObject sgo in stackGos){ if (sgo != null) GameObject.Destroy(sgo); }
            stackGos = null;
        }
        InventoryController.instance.RemoveInventory(this);
    }
    const int   ReservationExpireInterval = 120; // ticks between expiry sweeps per inventory
    const float ReservationMaxAge         = 60f;  // seconds before a reservation is considered stale
    const float MarketReservationMaxAge   = 120f; // longer timeout for market hauls (travel takes time)
    int _expireTick = 0;

    public void TickUpdate(){
        Decay();
        if (++_expireTick >= ReservationExpireInterval) {
            _expireTick = 0;
            float maxAge = invType == InvType.Market ? MarketReservationMaxAge : ReservationMaxAge;
            foreach (ItemStack stack in itemStacks)
                stack?.ExpireIfStale(maxAge);
        }
    }
    public void Decay(float time = 1f){
        float invTypeMult = invType switch {
            InvType.Floor      => 5f,
            InvType.Market     => 0f,
            InvType.Animal     => 0f,
            InvType.Equip      => 1f,
            InvType.Blueprint  => 0f,
            InvType.Reservoir  => 0f, // fuel items don't decay in building reserves
            InvType.Furnishing => 0f, // furnishing slots track their own per-slot lifetime instead
            _                  => 1f
        };
        if (invTypeMult == 0f) return;
        if (IsWet()) invTypeMult *= 2f;
        for (int i = 0; i < nStacks; i++){
            itemStacks[i].Decay(invTypeMult * time);
        }
    }

    // True while this floor inventory is still within its rain-soaked window.
    // Other inventory types are never wet (rain only reaches outdoor floor piles).
    public bool IsWet() {
        if (invType != InvType.Floor) return false;
        float now = World.instance?.timer ?? 0f;
        return wetUntil > now;
    }

    // Refresh the wet timer to (now + ticks), or no-op if a longer window is already set.
    // Called by WeatherSystem when rain hits an exposed floor inventory.
    public void MarkWet(float ticks) {
        if (invType != InvType.Floor) return;
        float newUntil = (World.instance?.timer ?? 0f) + ticks;
        if (newUntil > wetUntil) wetUntil = newUntil;
    }

    // ── Moving items ─────────────────────────────────────────────────────────

    // returns leftover size
    private int AddItem(Item item, int quantity, bool force = false){
        if (destroyed) {
            Debug.LogError($"Inventory.AddItem called on destroyed {invType} '{displayName}' at ({x},{y}) — stale reference (item={item?.name}, qty={quantity}). Returning no-op.");
            return quantity; // full "overflow" — nothing added
        }
        if (item == null) {Debug.LogError("tried adding null item"); return quantity;}
        if (item.children != null && item.children.Length > 0) {
            Debug.LogError($"Inventory.AddItem: '{item.name}' is a group item and cannot be added to inventories. Only leaf items may exist in inventories. (inv: '{displayName}', type: {invType}, pos: ({x},{y}))");
            return quantity;
        }
        if (!force && !ItemTypeCompatible(item) && quantity > 0){
            Debug.Log($"tried adding type-incompatible item {item.name} (isLiquid={item.isLiquid}) to {invType} '{displayName}' at ({x},{y})");
            return quantity;
        }
        if (!force && (locked || allowed[item.id] == false) && quantity > 0){
            string reason = locked ? "locked" : "disallowed";
            Debug.Log($"tried adding {reason} item {item.name} to {invType} '{displayName}' at ({x},{y})");
            return quantity;
        }
        // Build iteration order for consolidation:
        // 1. Existing stacks holding this item, fullest first (top off rather than spreading to a new slot)
        // 2. Empty stacks in original order (overflow into fresh slots as needed)
        // Stacks occupied by a different item are skipped — ItemStack.AddItem returns null for them anyway.
        // When slotConstraints is set and we're adding (quantity > 0), skip stacks whose intended
        // item doesn't match — this is what binds blueprint stacks to their cost slots so a smaller
        // cost can't squat in a slot sized for a larger one. Subtraction ignores constraints.
        bool checkSlotConstraints = slotConstraints != null && quantity > 0 && !force;
        var matchingIndices = new List<(int idx, int qty)>();
        var emptyIndices = new List<int>();
        for (int i = 0; i < nStacks; i++) {
            if (checkSlotConstraints && slotConstraints[i] != null && !MatchesItem(item, slotConstraints[i])) continue;
            if (itemStacks[i].item == item) matchingIndices.Add((i, itemStacks[i].quantity));
            else if (itemStacks[i].item == null) emptyIndices.Add(i);
        }
        matchingIndices.Sort((a, b) => b.qty.CompareTo(a.qty)); // fullest first

        var sortedIndices = new List<int>(nStacks);
        foreach (var (idx, _) in matchingIndices) sortedIndices.Add(idx);
        sortedIndices.AddRange(emptyIndices);

        foreach (int i in sortedIndices){
            Item prevItem = itemStacks[i].item;
            int? result = itemStacks[i].AddItem(item, quantity);
            if (result == null){ continue; } // shouldn't happen with the pre-filter, but guard anyway
            // Floor haul side effects: remove order when stack empties, register when items arrive.
            if (invType == InvType.Floor) {
                if (prevItem != null && itemStacks[i].item == null)
                    WorkOrderManager.instance?.RemoveHaulForStack(itemStacks[i]);
                else if (quantity > 0 && itemStacks[i].quantity > 0)
                    WorkOrderManager.instance?.RegisterHaul(itemStacks[i]);
            }
            // Storage eviction side effects.
            if (invType == InvType.Storage) {
                if (prevItem != null && itemStacks[i].item == null)
                    WorkOrderManager.instance?.RemoveHaulForStack(itemStacks[i]); // stack emptied — order no longer needed
                else if (force && allowed[item.id] == false && itemStacks[i].quantity > 0)
                    WorkOrderManager.instance?.RegisterStorageEvictionHaul(itemStacks[i]); // disallowed item force-added
            }
            quantity = result.Value; //set quantity to remaining size to get off
            if (quantity == 0){ break; }  // successfully added all items. stop.
        }
        UpdateSprite(); // this is a bit wasteful right now.
        return quantity; // leftover size
    }

    public int MoveItemTo(Inventory otherInv, Item item, int quantity){
        if (destroyed || otherInv == null || otherInv.destroyed) {
            Inventory dead = destroyed ? this : otherInv;
            string role = destroyed ? "source" : "destination";
            Debug.LogWarning($"Inventory.MoveItemTo called with destroyed {role} ({dead?.invType} '{dead?.displayName}' at ({dead?.x},{dead?.y})) — stale reference (item={item?.name}, qty={quantity}). Returning 0.");
            Debug.LogWarning($"  caller: {FormatCallerTrace("Inventory.MoveItemTo")}");
            return 0;
        }
        // Group items (e.g. "wood") can't exist as physical stacks — resolve to the leaf via GetLeafStack.
        // GetLeafStack (not GetItemStack) is used here because MoveItemTo is an execution step: the
        // caller already holds the reservation and must be able to draw down on it even if the stack is
        // fully reserved (Available() == false). GetItemStack's Available() guard is for planning only.
        if (item.children != null) {
            ItemStack stack = GetLeafStack(item);
            if (stack == null) return quantity; // nothing here
            item = stack.item;
        }
        int taken = quantity + AddItem(item, -quantity);
        int overFill = otherInv.AddItem(item, taken);
        if (overFill > 0){
            // Force the return so disallowed/locked source invs don't silently eat the leftover.
            // Items came from this inventory, so there is always room — LogError if not.
            int stillLost = AddItem(item, overFill, force: true);
            if (stillLost > 0)
                Debug.LogError($"MoveItemTo: {stillLost} fen of {item.name} lost returning to {invType} inv at ({x},{y}) — source had no room!");
        }
        int moved = taken - overFill;
        if (otherInv.invType == InvType.Market && moved > 0)
            WorkOrderManager.instance?.UpdateMarketOrders(otherInv);
        if (invType == InvType.Market && moved > 0)
            WorkOrderManager.instance?.UpdateMarketOrders(this);
        return moved;
    }
    public int MoveItemTo(Inventory otherInv, string name, int quantity){return MoveItemTo(otherInv, Db.itemByName[name], quantity);}

    // Like MoveItemTo but bypasses the allowed filter on the destination — use when items must not be lost
    // (e.g. migrating a floor inventory into a newly-placed storage building).
    // Haulers will eventually move the item out once they notice it is disallowed.
    // Do NOT use with market inventories — it skips UpdateMarketOrders.
    public int ForceMoveItemTo(Inventory otherInv, Item item, int quantity){
        int taken = quantity + AddItem(item, -quantity);
        int overFill = otherInv.AddItem(item, taken, force: true);
        if (overFill > 0){
            int stillLost = AddItem(item, overFill, force: true);
            if (stillLost > 0)
                Debug.LogError($"ForceMoveItemTo: {stillLost} fen of {item.name} lost returning to {invType} inv at ({x},{y}) — source had no room!");
        }
        return taken - overFill;
    }
    
    // adds to ginv. returns leftover size.
    public int Produce(Item item, int quantity = 1){
        int produced = quantity - AddItem(item, quantity);
        ginv.AddItem(item, produced);
        //Debug.Log("produced" + item.name + produced.ToString());
        if (invType == InvType.Market)
            WorkOrderManager.instance?.UpdateMarketOrders(this);
        return quantity - produced;
    }

    // ── Getting info ─────────────────────────────────────────────────────────

    // Returns true if `candidate` is `query` itself or any leaf descendant of it.
    // Used so that group items (e.g. "wood") act as wildcards matching any child (e.g. "oak", "pine").
    public static bool MatchesItem(Item candidate, Item query) {
        if (candidate == query) return true;
        if (query.children == null) return false;
        foreach (Item child in query.children)
            if (MatchesItem(candidate, child)) return true;
        return false;
    }

    public int Quantity(Item item){
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && MatchesItem(stack.item, item)){
                amount += stack.quantity;
            }
        }
        return amount;
    }
    public bool ContainsAvailableItem(Item item){
        if (item == null){ return !IsEmpty(); }
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && stack.quantity > 0 && stack.Available() && MatchesItem(stack.item, item)){
                return true;
            }
        }
        return false;
    }
    public bool ContainsItem(ItemQuantity iq, int n = 1){ return (Quantity(iq.item) >= iq.quantity*n);}
    public bool ContainsItems(ItemQuantity[] iqs, int n = 1){
        bool sufficient = true;
        foreach (ItemQuantity iq in iqs){
            if (Quantity(iq.item) < iq.quantity * n){
                sufficient = false;
            }
        }
        return sufficient;
    }
    public Item GetMostItem(){
        int most = 0;
        Item mostItem = null;
        foreach (ItemStack stack in itemStacks){
            if (stack.quantity > most){
                most = stack.quantity;
                mostItem = stack.item;
            }
        }
        return mostItem;
    }
    public ItemStack GetItemToHaul(){   // returns null if nothing, or item if something need to haul
        if (invType == InvType.Market || invType == InvType.Blueprint || invType == InvType.Reservoir || invType == InvType.Furnishing) return null;
        foreach (ItemStack stack in itemStacks){
            if (!stack.Empty() && stack.Available() &&
                (locked || allowed[stack.item.id] == false || invType == InvType.Floor)){
                return stack;
            }
        }
        return null;
    }
    public bool HasItemToHaul(Item item){ // if null, finds any item to haul
        if (invType == InvType.Market || invType == InvType.Blueprint || invType == InvType.Reservoir || invType == InvType.Furnishing) return false;
        foreach (ItemStack stack in itemStacks){
            if ((item == null || stack.item == item) && stack.quantity > 0 && stack.Available() &&
                (locked || allowed[stack.item.id] == false || invType == InvType.Floor)){
                return true;
            }
        }
        return false;
    }
    // How much space is available for item in this inventory (allowed Storage/Animal only).
    // Counts both empty stacks (any item could fill them) and partially-filled stacks of the same item.
    // Accounts for resSpace (destination reservations) so in-flight deliveries don't double-book space.
    public int GetStorageForItem(Item item){
        if (invType == InvType.Market || invType == InvType.Blueprint || invType == InvType.Reservoir || invType == InvType.Furnishing) return 0;
        if (!ItemTypeCompatible(item)) return 0;
        if (locked || allowed[item.id] == false || invType == InvType.Floor){return 0;}
        int space = 0;
        foreach (ItemStack stack in itemStacks)
            space += stack.FreeSpace(item);
        return space;
    }
    // Quantity not reserved by any task (usable for order placement checks).
    public int AvailableQuantity(Item item){
        int total = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack.item != null && MatchesItem(stack.item, item)) total += Math.Max(0, stack.quantity - stack.resAmount);
        }
        return total;
    }
    // Space in an existing partial stack of `item` (for floor consolidation).
    // Accounts for resSpace so in-flight deliveries don't double-book merge space.
    public int GetMergeSpace(Item item) {
        if (!ItemTypeCompatible(item)) return 0;
        if (locked || allowed[item.id] == false) return 0;
        foreach (ItemStack stack in itemStacks)
            if (stack.item == item) {
                int free = stack.FreeSpace(item);
                if (free > 0) return free;
            }
        return 0;
    }
    // Unlike GetStorageForItem, only checks stacks already holding this item (no empty stacks).
    // Use to top up an existing stack without claiming a new slot.
    // Accounts for resSpace so in-flight deliveries don't double-book space.
    public bool HasSpaceForItem(Item item){
        if (locked || invType == InvType.Market || invType == InvType.Blueprint) return false;
        foreach (ItemStack stack in itemStacks){
            if (stack.item == item && stack.FreeSpace(item) > 0)
                return true;
        }
        return false;
    }
    // ── Destination space reservations ──────────────────────────
    // Distributes a space reservation across stacks: matching partial first (fullest first),
    // then empty stacks. Returns the per-stack breakdown of what was actually reserved
    // (only stacks where >0 was reserved appear). Caller is responsible for releasing
    // each entry by calling stack.UnreserveSpace(amount) — typically via Task.Cleanup,
    // which records these tuples in Task.reservedSpaces.
    public List<(ItemStack stack, int amount)> ReserveSpace(Item item, int quantity, Task by = null) {
        var entries = new List<(ItemStack, int)>();
        if (quantity <= 0) return entries;
        // Build iteration order matching AddItem: matching stacks fullest-first, then empty
        var matchingIndices = new List<(int idx, int qty)>();
        var emptyIndices = new List<int>();
        for (int i = 0; i < nStacks; i++) {
            if (itemStacks[i].item == item && itemStacks[i].quantity > 0)
                matchingIndices.Add((i, itemStacks[i].quantity));
            else if (itemStacks[i].item == null || itemStacks[i].quantity == 0)
                emptyIndices.Add(i);
        }
        matchingIndices.Sort((a, b) => b.qty.CompareTo(a.qty)); // fullest first

        int remaining = quantity;
        foreach (var (idx, _) in matchingIndices) {
            if (remaining <= 0) break;
            int got = itemStacks[idx].ReserveSpace(item, remaining, by);
            if (got > 0) entries.Add((itemStacks[idx], got));
            remaining -= got;
        }
        foreach (int idx in emptyIndices) {
            if (remaining <= 0) break;
            int got = itemStacks[idx].ReserveSpace(item, remaining, by);
            if (got > 0) entries.Add((itemStacks[idx], got));
            remaining -= got;
        }
        return entries;
    }

    public int GetSpace(){ // only coutns empty stacks
        int amount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack.quantity == 0){
                amount += stack.stackSize;
            }
        }
        return amount;
    }
    public ItemStack GetItemStack(Item item){
        ItemStack best = null;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && stack.quantity > 0 && stack.Available() && MatchesItem(stack.item, item)){
                if (best == null || stack.quantity < best.quantity){ // prefer smallest — drains thin stacks first
                    best = stack;
                }
            }
        }
        return best;
    }
    // Like GetItemStack but does NOT require Available() — for use in MoveItemTo (execution),
    // where the caller holds the reservation and must be able to draw down on a fully-reserved stack.
    // Strategy: pick the leaf type with the highest combined quantity (avoids locking consumers to a
    // scarce type), then return its smallest individual stack (drains thin stacks first within that type).
    private ItemStack GetLeafStack(Item item){
        // Fast path: single-slot inventories (floor, animal, fuel, etc.).
        if (nStacks == 1) {
            var s = itemStacks[0];
            return s != null && s.item != null && s.quantity > 0 && MatchesItem(s.item, item) ? s : null;
        }
        // Find the leaf type with the highest combined quantity across all stacks.
        Item bestLeaf = null;
        int bestTotal = 0;
        foreach (ItemStack s in itemStacks) {
            if (s == null || s.item == null || s.quantity <= 0 || !MatchesItem(s.item, item)) continue;
            if (s.item == bestLeaf) continue; // already evaluated this leaf type
            int total = 0;
            foreach (ItemStack t in itemStacks)
                if (t != null && t.item == s.item) total += t.quantity;
            if (total > bestTotal) { bestTotal = total; bestLeaf = s.item; }
        }
        if (bestLeaf == null) return null;
        // Return the smallest stack of the winning leaf type.
        ItemStack best = null;
        foreach (ItemStack s in itemStacks)
            if (s != null && s.item == bestLeaf && s.quantity > 0)
                if (best == null || s.quantity < best.quantity) best = s;
        return best;
    }
    public bool HasDisallowedItem(){
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && allowed[stack.item.id] == false){
                return true;
            }
        }
        return false;
    }
    public bool IsEmpty(){
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.quantity > 0){
                return false; }}
        return true;
    }
    public List<Item> GetItemsList(){
        List<Item> items = new List<Item>();
        foreach (ItemStack stack in itemStacks) {
            if (stack != null && stack.item != null && stack.quantity > 0) {
                items.Add(stack.item);
            }
        }
        return items;
    }


    // ── Other ────────────────────────────────────────────────────────────────

    public void Restack(){
        var restackedInventory = new ItemStack[itemStacks.Length];
        int index = 0;
        foreach (var stack in itemStacks){
            if (stack.quantity == 0) continue;

            var matchingStack = restackedInventory.FirstOrDefault(s => s != null && s.item == stack.item);
            if (matchingStack != null){
                int spaceAvailable = matchingStack.stackSize - matchingStack.quantity;
                int quantityToAdd = Math.Min(spaceAvailable, stack.quantity);

                matchingStack.quantity += quantityToAdd;
                stack.quantity -= quantityToAdd;

                if (stack.quantity > 0){
                    restackedInventory[index++] = new ItemStack(this, stack.item, stack.quantity, stackSize);
                }
            }
            else {
                restackedInventory[index++] = new ItemStack(this, stack.item, stack.quantity, stackSize);
            }
        }
        itemStacks = restackedInventory;
    }

    public void AllowItem(Item item){
        allowed[item.id] = true;
        // Remove any pending eviction haul orders — item is welcome here again.
        if (invType == InvType.Storage)
            foreach (ItemStack stack in itemStacks)
                if (stack.item == item)
                    WorkOrderManager.instance?.RemoveHaulForStack(stack);
    }
    public void DisallowItem(Item item){
        allowed[item.id] = false;
        // Register eviction hauls for any stacks of this item already in this inventory.
        if (invType == InvType.Storage)
            foreach (ItemStack stack in itemStacks)
                if (stack.item == item && stack.quantity > 0)
                    WorkOrderManager.instance?.RegisterStorageEvictionHaul(stack);
    }
    public void ToggleAllowItem(Item item){
        allowed[item.id] = !allowed[item.id];
        if (invType == InvType.Storage) {
            foreach (ItemStack stack in itemStacks) {
                if (stack.item != item) continue;
                if (allowed[item.id] == false && stack.quantity > 0)
                    WorkOrderManager.instance?.RegisterStorageEvictionHaul(stack);
                else
                    WorkOrderManager.instance?.RemoveHaulForStack(stack);
            }
        }
    }
    // Toggles item and all its descendant leaf items together.
    public void ToggleAllowItemWithChildren(Item item){
        bool newState = !allowed[item.id];
        SetAllowRecursive(item, newState);
    }
    void SetAllowRecursive(Item item, bool state){
        if (state) AllowItem(item);
        else       DisallowItem(item);
        if (item.children != null)
            foreach (Item child in item.children)
                SetAllowRecursive(child, state);
    }
    // Allow all compatible items in this inventory.
    public void AllowAll(){
        foreach (Item item in Db.itemsFlat) {
            if (item == null) continue;
            if (!ItemTypeCompatible(item)) continue;
            AllowItem(item);
        }
    }
    // Disallow all items in this inventory.
    public void DenyAll(){
        foreach (Item item in Db.itemsFlat) {
            if (item == null) continue;
            DisallowItem(item);
        }
    }
    // Copies allowed state from another inventory's allowed dictionary.
    public void PasteAllowed(Dictionary<int, bool> source){
        foreach (var kvp in source) {
            if (!allowed.ContainsKey(kvp.Key)) continue;
            if (kvp.Value) AllowItem(Db.items[kvp.Key]);
            else           DisallowItem(Db.items[kvp.Key]);
        }
    }

    public enum ItemSpriteType { Icon, Floor, Storage }

    // ── Floor-item sort order ──────────────────────────────────────────
    // Items resting on a tile pick their sortingOrder based on what's directly
    // below: a building's solid top → 12, a platform's solid top → 17, anything
    // else → 70 (dirt or fallback). Platforms render above buildings at the
    // same tile, so platform takes priority when both are present below.
    // We bump by +2 (not +1) so that wheel/blade overlays at parent+1 don't
    // collide with the floor pile sitting on the same surface.
    // See SPEC-rendering.md sorting-order table.
    private int ComputeFloorSortingOrder(){
        Tile below = World.instance?.GetTileAt(x, y - 1);
        if (below == null) return 70;
        if (below.structs[1] != null && below.structs[1].structType.solidTop) return 17;
        if (below.building   != null && below.building.structType.solidTop)   return 12;
        return 70;
    }

    // Re-evaluate the floor inventory's sortingOrder. Called when the surface
    // beneath this tile changes (structure built/destroyed, tile type changed).
    public void RefreshFloorSortingOrder(){
        if (invType != InvType.Floor || destroyed || go == null) return;
        SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
        if (sr == null) return;
        sr.sortingOrder = ComputeFloorSortingOrder();
        LightReceiverUtil.SetSortBucket(sr);
    }

    // Refresh the floor inv at (x, y), if any. No-op if the tile has no inv or
    // a non-Floor inv. Convenience for code that mutates surfaces and needs to
    // re-sort the pile sitting on top.
    public static void RefreshFloorAt(int x, int y){
        Tile t = World.instance?.GetTileAt(x, y);
        if (t?.inv == null || t.inv.invType != InvType.Floor) return;
        t.inv.RefreshFloorSortingOrder();
    }

    public void UpdateSprite(){
        if (destroyed) {
            Debug.LogError($"Inventory.UpdateSprite called on destroyed {invType} '{displayName}' at ({x},{y}) — stale reference. Skipping.");
            return;
        }
        if (invType == InvType.Animal || invType == InvType.Market || invType == InvType.Equip || invType == InvType.Blueprint || invType == InvType.Reservoir || invType == InvType.Furnishing) return;
        if (stackGos != null){
            // Multi-stack storage (drawer): update each slot independently
            for (int i = 0; i < nStacks && i < stackGos.Length; i++){
                if (stackGos[i] == null) continue;
                ItemStack stack = itemStacks[i];
                SpriteRenderer sr = stackGos[i].GetComponent<SpriteRenderer>();
                if (stack == null || stack.Empty()){
                    stackGos[i].name = "inventorystack_empty";
                    sr.sprite = null;
                    continue;
                }
                string sName = stack.item.name.Trim().Replace(" ", "");
                float qFill = stack.quantity / (float)stack.stackSize;
                string qVariant = qFill >= 0.75f ? "qhigh" : qFill < 0.2f ? "qlow" : "qmid";
                Sprite sSprite  = Resources.Load<Sprite>($"Sprites/Items/split/{sName}/{qVariant}");
                sSprite ??= Resources.Load<Sprite>($"Sprites/Items/split/{sName}/qmid");
                sSprite ??= Resources.Load<Sprite>($"Sprites/Items/split/default/{qVariant}");
                sSprite ??= Resources.Load<Sprite>("Sprites/Items/split/default/qmid");
                stackGos[i].name = "inventorystack_" + sName;
                sr.sprite = sSprite;
            }
            return;
        }
        if (IsEmpty()) {
            go.name = "inventory_empty";
            go.GetComponent<SpriteRenderer>().sprite = null;
            return;
        }
        // Bookshelves: all slots treated as one visual stack. Total fill across every slot
        // picks one of three shared sprites (slow/smid/shigh). Books aren't rendered as
        // individual item icons — the shelf itself is the visual.
        if (invType == InvType.Storage && storageClass == ItemClass.Book) {
            int totalFen = 0;
            int totalCap = 0;
            foreach (ItemStack stack in itemStacks) {
                totalFen += stack.quantity;
                totalCap += stack.stackSize;
            }
            float fillRatio = totalCap > 0 ? totalFen / (float)totalCap : 0f;
            string sVariant = fillRatio >= 0.75f ? "shigh" : fillRatio < 0.2f ? "slow" : "smid";
            Sprite bookSprite = Resources.Load<Sprite>($"Sprites/Items/split/books/{sVariant}");
            bookSprite ??= Resources.Load<Sprite>("Sprites/Items/split/books/icon"); // fallback if fill-level art missing
            go.name = "inventory_books_" + sVariant;
            go.GetComponent<SpriteRenderer>().sprite = bookSprite;
            return;
        }
        Item mostItem = null;
        int mostAmount = 0;
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.item != null && stack.quantity > mostAmount){
                mostItem = stack.item;
                mostAmount = stack.quantity;
            }
        }
        // Book-class items all share one sprite folder ("books") — there's no per-book art,
        // so floor drops / non-bookshelf displays of individual books route through that
        // shared folder rather than the per-item name path (which has no matching files).
        String iName = mostItem.itemClass == ItemClass.Book
            ? "books"
            : mostItem.name.Trim().Replace(" ", "");
        float fill = mostAmount / (float)stackSize;
        Sprite sprite;
        if (invType == InvType.Floor) {
            sprite = Resources.Load<Sprite>($"Sprites/Items/split/{iName}/floor");
        } else if (invType == InvType.Storage) {
            // Liquid storage (tanks): the water shader renders the fill via WaterController's
            // decorative-zone pipeline, scaled continuously to stored quantity. Don't draw
            // the generic slow/smid/shigh sprite on top — it would occlude the shader.
            if (isLiquidStorage) {
                go.GetComponent<SpriteRenderer>().sprite = null;
                go.name = "inventory_liquid";
                return;
            }
            string sVariant = fill >= 0.75f ? "shigh" : fill < 0.2f ? "slow" : "smid";
            sprite  = Resources.Load<Sprite>($"Sprites/Items/split/{iName}/{sVariant}");
            sprite ??= Resources.Load<Sprite>($"Sprites/Items/split/{iName}/smid");
            sprite ??= Resources.Load<Sprite>($"Sprites/Items/split/default/{sVariant}");
            sprite ??= Resources.Load<Sprite>("Sprites/Items/split/default/smid");
        } else {
            sprite = Resources.Load<Sprite>($"Sprites/Items/split/{iName}/icon");
        }
        sprite ??= Resources.Load<Sprite>($"Sprites/Items/split/{iName}/icon");
        sprite ??= Resources.Load<Sprite>("Sprites/Items/split/default/icon");
        go.name = "inventory_" + mostItem.name;
        go.GetComponent<SpriteRenderer>().sprite = sprite;
    }

    public override string ToString(){
        string str = "";
        foreach (ItemStack stack in itemStacks){
            if (stack != null && stack.quantity > 0){
                str += stack.ToString();
            }
        }
        return str;
    }


}