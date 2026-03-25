using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public class InfoPanel : MonoBehaviour {
    public GameObject textDisplayPrefab;
    public static InfoPanel instance { get; protected set; }
    public GameObject textDisplayGo;
    public object obj;
    public GameObject animalHighlight;    // assign in Inspector; follows selected animal
    public GameObject tileHighlight;      // assign in Inspector; overlays selected tile
    [SerializeField] Button priorityUpButton;      // assign in Inspector
    [SerializeField] Button priorityDownButton;    // assign in Inspector
    [SerializeField] Button workerSlotsUpButton;   // assign in Inspector; shown for multi-slot workstations
    [SerializeField] Button workerSlotsDownButton; // assign in Inspector
    private Animal selectedAnimal;

    public enum InfoMode {
        Inactive,
        Tile, 
        Building,
        Blueprint,
        Animal, 
        Plant,
    }
    public InfoMode infoMode = InfoMode.Inactive;

    public void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one " + this.GetType().ToString());}
        instance = this;
        ShowInfo(null); // initialize as inactive
        //textDisplayGo = Instantiate(textDisplayPrefab, this.transform);
        if (priorityUpButton != null)      priorityUpButton.onClick.AddListener(     () => ChangeBlueprintPriority(1));
        if (priorityDownButton != null)    priorityDownButton.onClick.AddListener(   () => ChangeBlueprintPriority(-1));
        if (workerSlotsUpButton != null)   workerSlotsUpButton.onClick.AddListener(  () => ChangeWorkerSlots(1));
        if (workerSlotsDownButton != null) workerSlotsDownButton.onClick.AddListener(() => ChangeWorkerSlots(-1));
    }

    void ChangeBlueprintPriority(int delta) {
        if (obj is Tile tile) {
            Blueprint bp = tile.GetAnyBlueprint();
            if (bp != null) {
                bp.priority = Mathf.Max(0, bp.priority + delta);
                UpdateInfo();
            }
        }
    }

    void SetPriorityButtonsVisible(bool visible) {
        if (priorityUpButton != null)   priorityUpButton.gameObject.SetActive(visible);
        if (priorityDownButton != null) priorityDownButton.gameObject.SetActive(visible);
    }

    void SetWorkerSlotsButtonsVisible(bool visible) {
        if (workerSlotsUpButton != null)   workerSlotsUpButton.gameObject.SetActive(visible);
        if (workerSlotsDownButton != null) workerSlotsDownButton.gameObject.SetActive(visible);
    }

    void ChangeWorkerSlots(int delta) {
        if (obj is Tile tile && tile.building is Building building) {
            if (!building.structType.isWorkstation || building.structType.capacity <= 1) return;
            var order = WorkOrderManager.instance?.FindOrdersForBuilding(building)
                .FirstOrDefault(o => o.type == WorkOrderManager.OrderType.Craft);
            if (order == null) return;
            order.res.effectiveCapacity = Mathf.Clamp(
                order.res.effectiveCapacity + delta, 0, order.res.capacity);
            UpdateInfo();
        }
    }

    public void ShowInfo(object obj){
        this.obj = obj;
        UpdateInfo();
    }
    public void UpdateInfo(){
        // todo: make it so if you click again it cycles possible targets somehow?
        if (obj == null){
            Deselect();
            return;
        }
        SetPriorityButtonsVisible(false);    // hide by default; shown only for blueprints below
        SetWorkerSlotsButtonsVisible(false); // hide by default; shown only for multi-slot workstations

        if (obj is List<Animal> animals) {
            infoMode = InfoMode.Animal;
            gameObject.SetActive(true);
            selectedAnimal = animals.Count > 0 ? animals[0] : null;
            if (animalHighlight != null) animalHighlight.SetActive(selectedAnimal != null);
            if (tileHighlight != null) tileHighlight.SetActive(false);

            static string FormatSlot(Inventory slot) {
                var s = slot.itemStacks[0];
                return s.item != null ? s.item.name + " " + ItemStack.FormatQ(s.quantity, s.item.discrete) : "empty";
            }
            static string FormatAnimal(Animal ani) {
                string t = "animal: " + ani.aName +
                    "\n state: " + ani.state.ToString() +
                    "\n job: " + ani.job.name +
                    "\n [food] " + FormatSlot(ani.foodSlotInv) +
                    "\n [tool] " + FormatSlot(ani.toolSlotInv) +
                    "\n inv: " + ani.inv.ToString();
                t += "\n task: " + (ani.task?.ToString() ?? "none");
                t += "\n objective: " + (ani.task?.currentObjective?.ToString() ?? "none");
                if (ani.task is CraftTask craftTask)
                    t += "\n recipe: " + craftTask.recipe?.description;
                t += "\n location: " + ani.go.transform.position.ToString() +
                    "\n efficiency: " + ani.efficiency.ToString("F2") +
                    "\n fullness: " + ani.eating.Fullness().ToString("F2") +
                    "\n eep: " + ani.eeping.Eepness().ToString("F2") +
                    "\n happiness: " + ani.happiness.ToString();
                t += "\n skills:";
                foreach (Skill sk in System.Enum.GetValues(typeof(Skill))) {
                    int   lv        = ani.skills.GetLevel(sk);
                    float xp        = ani.skills.GetXp(sk);
                    float threshold = SkillSet.XpThreshold(lv);
                    t += $"\n  {sk.ToString().ToLower()}: lv{lv} ({xp:F1}/{threshold:F0} xp)";
                }
                return t;
            }

            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < animals.Count; i++) {
                if (i > 0) parts.Append("\n---\n");
                parts.Append(FormatAnimal(animals[i]));
            }
            textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = parts.ToString();
        }

        else if (obj is Tile tile) {
            selectedAnimal = null;
            infoMode = InfoMode.Tile;
            gameObject.SetActive(true);
            if (animalHighlight != null) animalHighlight.SetActive(false);
            if (tileHighlight != null) {
                tileHighlight.SetActive(true);
                tileHighlight.transform.position = new Vector3(tile.x, tile.y, -1);
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("location: " + tile.x + ", " + tile.y);

            // Nav info (above buildings)
            sb.Append("\nstandable: " + tile.node.standable + "  neighbors: " + tile.node.neighbors.Count);

            // Tile type info (only if non-default)
            if (tile.type.id != 0) {
                sb.Append("\ntile: " + tile.type.name +
                    "  solid: " + tile.type.solid);
            }

            if (tile.water > 0)
                sb.Append($"\nwater: {tile.water}/{WaterController.WaterMax}");

            string[] layerLabels = { "b", "m", "f", "r" };
            for (int d = 0; d < 4; d++) {
                Structure s = tile.structs[d];
                if (s != null) {
                    if (s is Plant plant) {
                        sb.Append("\n" + layerLabels[d] + ": " + s.structType.name);
                        sb.Append("\n  growth: " + plant.growthStage);
                        if (s.res != null) sb.Append("\n  res: " + s.res.reserved + "/" + s.res.capacity);
                        AppendTileOrders(sb, plant.tile);
                    } else {
                        sb.Append("\n" + layerLabels[d] + ": " + s.structType.name);
                        if (s.res != null) sb.Append("\n res: " + s.res.reserved + "/" + s.res.capacity);
                        if (s is Building bldg) {
                            if (bldg.structType.depleteAt > 0)
                                sb.Append("\n  uses: " + bldg.uses + "/" + bldg.structType.depleteAt);
                            if (bldg.fuelInv != null) {
                                int fuelQty = bldg.fuelInv.Quantity(bldg.structType.fuelItem);
                                sb.Append($"\n  fuel: {ItemStack.FormatQ(fuelQty)}/{ItemStack.FormatQ(bldg.structType.fuelCapacity)} {bldg.structType.fuelItemName}");
                            }
                            // tile-keyed orders (e.g. research on lab)
                            AppendTileOrders(sb, bldg.tile);
                            // building-keyed orders (e.g. craft on workstations)
                            AppendBuildingOrders(sb, bldg);
                            // inv-keyed orders (e.g. market hauls)
                            if (bldg.storage != null)
                                AppendInvOrders(sb, bldg.storage);
                        }
                    }
                }
                Blueprint bp = tile.blueprints[d];
                if (bp != null) {
                    sb.Append("\nblueprint (" + layerLabels[d] + "): " + bp.structType.name);
                    sb.Append("\n  priority: " + bp.priority + "  progress: " + bp.GetProgress());
                    var bpOrder = WorkOrderManager.instance?.FindOrderForBlueprint(bp);
                    if (bpOrder != null) sb.Append("\n  wo: " + bpOrder.type + " " + bpOrder.res.reserved + "/" + bpOrder.res.capacity);
                    SetPriorityButtonsVisible(true);
                }
            }

            // Show worker slot buttons for multi-slot workstations
            Building selBuilding = tile.building;
            if (selBuilding != null && selBuilding.structType.isWorkstation && selBuilding.structType.capacity > 1)
                SetWorkerSlotsButtonsVisible(true);

            // Inventory
            if (tile.inv != null) {
                sb.Append("\n inv:");
                foreach (var stack in tile.inv.itemStacks) {
                    if (stack.item != null) {
                        string resStr = stack.resAmount > 0 ? " (r" + ItemStack.FormatQ(stack.resAmount, stack.item.discrete) + ")" : "";
                        var stackOrder = WorkOrderManager.instance?.FindOrderForStack(stack);
                        string woStr = stackOrder == null ? "" :
                            stackOrder.type == WorkOrderManager.OrderType.Haul && stackOrder.priority == 1
                                ? $" [wo:Haul! {stackOrder.res.reserved}/{stackOrder.res.capacity}]"
                                : $" [wo:{stackOrder.type} {stackOrder.res.reserved}/{stackOrder.res.capacity}]";
                        sb.Append("\n  " + stack.item.name + " x " + ItemStack.FormatQ(stack.quantity, stack.item.discrete) + resStr + woStr);
                    }
                }
            }

            textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = sb.ToString();
        }
        else { selectedAnimal = null; Deselect(); }
    }

    // Appends "wo: X (r/c), Y (r/c)" for all orders keyed by tile (harvest, research). No-ops if empty.
    private static void AppendTileOrders(System.Text.StringBuilder sb, Tile tile) {
        if (WorkOrderManager.instance == null) return;
        var found = new System.Collections.Generic.List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForTile(tile))
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}");
        if (found.Count > 0)
            sb.Append("\n  wo: " + string.Join(", ", found));
    }

    // Appends "wo: X (r/c)" for all orders keyed by building (craft). No-ops if empty.
    // When effectiveCapacity < capacity, shows three-part "reserved/effective/max" to make the
    // player-set limit visible: e.g. "Craft 1/1/2" means 1 working, limit 1, max 2.
    private static void AppendBuildingOrders(System.Text.StringBuilder sb, Building building) {
        if (WorkOrderManager.instance == null) return;
        var found = new System.Collections.Generic.List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForBuilding(building)) {
            string active = o.isActive != null && !o.isActive() ? " [inactive]" : "";
            string capStr = o.res.effectiveCapacity < o.res.capacity
                ? $"{o.res.reserved}/{o.res.effectiveCapacity}/{o.res.capacity}"
                : $"{o.res.reserved}/{o.res.capacity}";
            found.Add($"{o.type} {capStr}{active}");
        }
        if (found.Count > 0)
            sb.Append("\n  wo: " + string.Join(", ", found));
    }

    // Appends "wo: X (r/c), Y (r/c)" for all orders keyed by inventory (market hauls). No-ops if empty.
    private static void AppendInvOrders(System.Text.StringBuilder sb, Inventory inv) {
        if (WorkOrderManager.instance == null) return;
        var found = new System.Collections.Generic.List<string>();
        foreach (var o in WorkOrderManager.instance.FindOrdersForInv(inv))
            found.Add($"{o.type} {o.res.reserved}/{o.res.capacity}");
        if (found.Count > 0)
            sb.Append("\n  wo: " + string.Join(", ", found));
    }

    public void Deselect(){
        infoMode = InfoMode.Inactive;
        gameObject.SetActive(false);
        SetPriorityButtonsVisible(false);
        if (animalHighlight != null) animalHighlight.SetActive(false);
        if (tileHighlight != null) tileHighlight.SetActive(false);
    }

    void Update(){
        if (infoMode == InfoMode.Animal && selectedAnimal != null && animalHighlight != null)
            animalHighlight.transform.position = selectedAnimal.go.transform.position + new Vector3(0, 0.6f, -1);
    }

}