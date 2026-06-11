using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.Serialization;

// Loads before anything else — Awake() runs before World.Start().
public class Db : MonoBehaviour {
    public static Db instance {get; protected set;}
    // itemById or jobById is just the array items or jobs.
    public static Dictionary<string, int> iidByName {get; protected set;}
    public static Dictionary<string, Item> itemByName {get; protected set;}
    public static Dictionary<string, Job> jobByName {get; protected set;}
    public static Dictionary<string, StructType> structTypeByName {get; protected set;}
    public static Dictionary<string, PlantType> plantTypeByName {get; protected set;}
    public static Dictionary<string, TileType> tileTypeByName {get; protected set;}
    public static Dictionary<string, FlowerType> flowerTypeByName {get; protected set;}

    public static Item[] items = new Item[500];
    public static Item[] itemsFlat = new Item[500];
    public static int itemsCount = 0;
    public static List<Item> edibleItems;
    // Edible items that are also a planting cost (some PlantType.costs entry). Mice avoid
    // eating the last few of these so farmers/loggers aren't left unable to replant — see
    // Animal.FindFood. Derived from plant data in LoadAll; no JSON flag.
    public static HashSet<Item> seedItems;
    public static List<Item> equipmentItems;
    public static List<Item> clothingItems;
    // Leaf items that fit into a named furnishing slot. Built at Db.LoadAll time by
    // walking itemsFlat and bucketing by `Item.furnishingSlot` (which cascades from
    // parent groups via AddItemToDb). Keys are slot names (e.g. "cloth"); a slot on a
    // building accepts any leaf in the matching bucket. Empty bucket = nothing buildable.
    public static Dictionary<string, List<Item>> itemsByFurnishingSlot;

    // All unique happiness satisfaction keys defined across items, buildings, and hardcoded sources.
    // Built at startup; used by Happiness.cs and both happiness panels to auto-discover needs.
    public static HashSet<string> happinessNeeds;
    public static List<string> happinessNeedsSorted; // stable ordering for panel display
    public static int happinessMaxScore; // happinessNeeds.Count + 1 (housing) + 2 (temp max) + 4 (food storage) + ceil(maxFurnishingPerMouse)
    // Max furnishing happiness an animal could get if they lived in the most generously
    // furnished house type with the highest-happiness item in every slot. Computed from
    // structTypes × itemsByFurnishingSlot at Db.LoadAll time. Drives the bar scale on the
    // GlobalHappinessPanel's furnishing row and contributes to happinessMaxScore.
    public static float maxFurnishingPerMouse;
    // Largest furnishingCostFen across all furnishing leaf items. FurnishingSlots sizes every slot
    // inventory to this so any furnishing — including a heavy discrete one (a stool) — fits as one
    // whole unit. Computed in BuildFurnishingSlotRegistry.
    public static int maxFurnishingCostFen;

    // Preferred display order for the happiness panel.
    // Food needs first, then decoration, then social, then leisure.
    // Needs not in this list are appended alphabetically at the end (future-proofing).
    private static readonly string[] happinessNeedsDisplayOrder = {
        "wheat", "rice", "fruit", "soymilk", "dairy",  // food
        "fountain",                    // decoration
        "social",                      // social
        "fireplace", "bench", "reading", "alcohol", // leisure
    };

    // Largest decorRadius across all structTypes. Computed at startup; used by Animal.ScanForNearbyDecorations.
    public static int maxDecoScanRadius;
    public static Job[] jobs = new Job[100];
    public static Recipe[] recipes = new Recipe[500];
    // Processor recipes (passive timed conversions — see ProcessorRecipe / Processor.cs),
    // loaded from processorRecipesDb.json and bucketed by the building that runs them.
    // One building = one recipe today; GetProcessorRecipe resolves the building's recipe.
    public static Dictionary<string, List<ProcessorRecipe>> processorRecipesByBuilding;
    public static StructType[] structTypes = new StructType[600];
    public static PlantType[] plantTypes = new PlantType[600];
    public static TileType[] tileTypes = new TileType[100];
    // Flowers are decorative-only (no Structure registry, no save state). The flat
    // list is kept for FlowerController's weighted-pick loop; flowerTypeByName lets
    // future systems (e.g. a harvestability upgrade path) look up a type by name.
    public static FlowerType[] flowerTypes = new FlowerType[64];
    public static int flowerTypesCount = 0;

    // Mouse name pools loaded from Resources/Misc/names.csv.
    public static List<string> chineseNames = new List<string>();
    public static List<string> inventedNames = new List<string>();

    // Runtime-generated mapping: research tech id → item id of the book that aids that tech.
    // Populated in GenerateBookItems() during Awake. Used by the scientist study task (M5) to
    // look up which book to fetch for a given tech.
    public static Dictionary<int, int> bookItemIdByTechId = new Dictionary<int, int>();

    // Runtime-generated mapping: research tech id → recipe id of the scribe recipe for that
    // tech's book. Populated in GenerateBookRecipes(). Used by ResearchSystem to inject the
    // recipe-unlock entry onto the matching tech so the recipe is gated by its own tech.
    public static Dictionary<int, int> bookRecipeIdByTechId = new Dictionary<int, int>();

    // Cached tech list from the first researchDb.json parse in GenerateBookItems, reused by
    // GenerateBookRecipes so we only parse the file once. Null if the parse failed.
    private static ResearchNodeData[] _cachedTechs;

    // Sprite sorting orders: see SPEC-rendering.md § "Sorting orders" for the authoritative list.

    private Db(){
        if (instance != null){
            Debug.LogError("tried to create two instances of database"); }
        instance = this;
        ResetCollections();
    }

    // Fires before any Awake on every play press, regardless of the Enter Play
    // Mode "Reload Domain" setting. With Reload Domain disabled (this project's
    // fast-play config), the constructor only runs on the very first scene load —
    // so on re-entering play, the existing Db MonoBehaviour persists with its
    // statics still populated from last session, and LoadAll's AddItemToDb fires
    // duplicate-id errors. SubsystemRegistration guarantees a clean reset every
    // play press. See project_plain_csharp_singletons.md (memory) for the pattern.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() {
        ResetCollections();
    }

    // Reset every static collection so a fresh LoadAll produces clean state.
    // Called from the ctor (first play + editor-mode TileAtlasBaker.EnsureDbLoaded)
    // and from the SubsystemRegistration hook (subsequent play presses).
    static void ResetCollections() {
        iidByName = new Dictionary<string, int>();
        itemByName = new Dictionary<string, Item>();
        jobByName = new Dictionary<string, Job>();
        structTypeByName = new Dictionary<string, StructType>();
        tileTypeByName = new Dictionary<string, TileType>();
        plantTypeByName = new Dictionary<string, PlantType>();
        flowerTypeByName = new Dictionary<string, FlowerType>();

        items      = new Item[500];
        itemsFlat  = new Item[500];
        itemsCount = 0;
        jobs       = new Job[100];
        recipes    = new Recipe[500];
        structTypes = new StructType[600];
        plantTypes  = new PlantType[600];
        tileTypes   = new TileType[100];
        flowerTypes = new FlowerType[64];
        flowerTypesCount = 0;
        bookRecipeIdByTechId = new Dictionary<int, int>();
        bookItemIdByTechId   = new Dictionary<int, int>();
        itemsByFurnishingSlot = new Dictionary<string, List<Item>>();
        processorRecipesByBuilding = new Dictionary<string, List<ProcessorRecipe>>();
        // ReadJson Add()s into chineseNames/inventedNames — reset so reloads don't
        // double the pool (which would shift Rng-based name selection deterministically
        // wrong, breaking snapshot reproducibility).
        chineseNames.Clear();
        inventedNames.Clear();
    }

    void Awake(){ // this runs before Start() like in world
        LoadAll();
    }

    // Public so editor tools (e.g. TileAtlasBaker) can populate statics in
    // edit mode where Awake doesn't fire. Idempotent: calling twice replaces
    // the populated state (the constructor has already reset the static
    // collections by this point).
    public void LoadAll() {
        ReadJson();
        GenerateBookItems();
        GenerateBookRecipes();
        WarnLongRecipeNames();
        itemsFlat = itemsFlat.Take(itemsCount).ToArray();
        edibleItems = itemsFlat.Where(i => i.foodValue > 0).OrderByDescending(i => i.foodValue).ToList();
        // An edible counts as a "seed" if some plant lists it as a planting cost. PlantType.costs
        // is resolved in PlantType.OnDeserialized (ReadJson, above), so it's populated here.
        seedItems = new HashSet<Item>();
        foreach (PlantType pt in plantTypes) {
            if (pt?.costs == null) continue;
            foreach (ItemQuantity c in pt.costs)
                if (c.item != null && c.item.foodValue > 0) seedItems.Add(c.item);
        }
        equipmentItems = itemsFlat.Where(i => { Item cur = i; while (cur != null) { if (cur.name == "tools") return true; cur = cur.parent; } return false; }).ToList();
        clothingItems = itemsFlat.Where(i => { Item cur = i; while (cur != null) { if (cur.name == "clothing") return true; cur = cur.parent; } return false; }).ToList();
        BuildFurnishingSlotRegistry();
        BuildHappinessNeedRegistry();
        ValidateNoGroupOutputs();
        ValidateDiscreteUnitsFit();
        ValidateProcessorRecipes();
        LoadItemIcons();
        LoadNames();
        Debug.Log("db loaded");
    }

    // Buckets every leaf item by its `furnishingSlot` so WOM dispatch and FurnishingSlots
    // can look up "what items fit a 'cloth' slot?" in O(1). Items inherit furnishingSlot
    // from their group parent (see AddItemToDb), so authors only tag the group.
    void BuildFurnishingSlotRegistry() {
        itemsByFurnishingSlot = new Dictionary<string, List<Item>>();
        maxFurnishingCostFen = 0;
        foreach (Item item in itemsFlat) {
            if (item == null || item.IsGroup) continue; // only leaves can be installed
            if (string.IsNullOrEmpty(item.furnishingSlot)) continue;
            if (!itemsByFurnishingSlot.TryGetValue(item.furnishingSlot, out var list)) {
                list = new List<Item>();
                itemsByFurnishingSlot[item.furnishingSlot] = list;
            }
            list.Add(item);
            // Validate the install cost holds at least one whole unit, and track the largest
            // so FurnishingSlots can size every slot to fit any furnishing.
            int costFen = item.furnishingCostFen;
            if (costFen <= 0) {
                string hint = item.discrete ? $" A discrete furnishing needs a cost of at least one unit ({item.unitWeight} liang)." : "";
                Debug.LogError($"Db validation: furnishing '{item.name}' resolves to a {costFen}-fen install cost (furnishingCost {item.furnishingCost} liang) — it cannot be installed.{hint}");
            }
            if (costFen > maxFurnishingCostFen) maxFurnishingCostFen = costFen;
        }
    }

    // Collects all unique happiness satisfaction keys from items (happinessNeed),
    // buildings (decorationNeed, leisureNeed), and hardcoded sources (social).
    void BuildHappinessNeedRegistry() {
        happinessNeeds = new HashSet<string>();
        foreach (Item item in edibleItems)
            if (!string.IsNullOrEmpty(item.happinessNeed))
                happinessNeeds.Add(item.happinessNeed);
        foreach (StructType st in structTypes) {
            if (st == null) continue;
            if (!string.IsNullOrEmpty(st.decorationNeed)) happinessNeeds.Add(st.decorationNeed);
            if (!string.IsNullOrEmpty(st.leisureNeed))    happinessNeeds.Add(st.leisureNeed);
        }
        happinessNeeds.Add("social");  // ChatTask — not data-driven
        happinessNeeds.Add("reading"); // ReadBookTask — not data-driven (no leisure building backs it)
        happinessNeeds.Add("alcohol"); // DrinkTask — not data-driven (wine is consumed wherever stored)

        // Build sorted list using manual display order; unknown future needs fall through alphabetically.
        happinessNeedsSorted = new List<string>();
        foreach (string need in happinessNeedsDisplayOrder)
            if (happinessNeeds.Contains(need)) happinessNeedsSorted.Add(need);
        foreach (string need in happinessNeeds.OrderBy(n => n))
            if (!happinessNeedsSorted.Contains(need)) happinessNeedsSorted.Add(need);

        // Furnishing happiness ceiling: best-case per mouse if their house has every slot
        // filled with the highest-happiness item. Scales the GlobalHappinessPanel bar and
        // counts toward the overall score cap.
        maxFurnishingPerMouse = 0f;
        foreach (StructType st in structTypes) {
            if (st?.furnishingSlotNames == null) continue;
            float sum = 0f;
            foreach (string slotName in st.furnishingSlotNames) {
                float bestForSlot = 0f;
                if (itemsByFurnishingSlot != null && itemsByFurnishingSlot.TryGetValue(slotName, out var items))
                    foreach (Item it in items)
                        if (it.furnishingHappiness > bestForSlot) bestForSlot = it.furnishingHappiness;
                sum += bestForSlot;
            }
            if (sum > maxFurnishingPerMouse) maxFurnishingPerMouse = sum;
        }
        // +1 housing, +2 temp max, +4 colony food storage (see AnimalController.MaxFoodStorageBonus), + furnishing ceiling
        happinessMaxScore = happinessNeeds.Count + 1 + 2 + Mathf.CeilToInt(AnimalController.MaxFoodStorageBonus) + Mathf.CeilToInt(maxFurnishingPerMouse);
        if (happinessNeeds.Contains("alcohol")) happinessMaxScore += 1; // the alcohol need scores +2 when satisfied, not +1

        maxDecoScanRadius = 0;
        foreach (StructType st in structTypes)
            if (st != null && st.decorRadius > maxDecoScanRadius)
                maxDecoScanRadius = st.decorRadius;
    }

    void LoadNames() {
        TextAsset csv = Resources.Load<TextAsset>("Misc/names");
        if (csv == null) { Debug.LogError("Db: names.csv not found at Resources/Misc/names"); return; }
        foreach (string line in csv.text.Split('\n')) {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("name")) continue; // skip header
            string[] parts = trimmed.Split(',');
            if (parts.Length != 2) continue;
            string n = parts[0].Trim();
            string pool = parts[1].Trim();
            if (pool == "chinese") chineseNames.Add(n);
            else if (pool == "invented") inventedNames.Add(n);
            else Debug.LogWarning($"Db: unknown pool '{pool}' for name '{n}'");
        }
    }

    // Draw a random mouse name from the combined pool, avoiding names already in use.
    // Falls back to "mouse0", "mouse1", ... if all pool names are taken.
    public static string DrawName(HashSet<string> usedNames = null) {
        int total = chineseNames.Count + inventedNames.Count;
        if (total == 0) { Debug.LogError("Db: name pool is empty, falling back to 'mouse'"); return "mouse"; }
        if (usedNames == null || usedNames.Count == 0) {
            int i = Rng.Range(0, total);
            return i < chineseNames.Count ? chineseNames[i] : inventedNames[i - chineseNames.Count];
        }
        // Build list of available names
        List<string> available = new List<string>();
        foreach (string n in chineseNames) if (!usedNames.Contains(n)) available.Add(n);
        foreach (string n in inventedNames) if (!usedNames.Contains(n)) available.Add(n);
        if (available.Count > 0) return available[Rng.Range(0, available.Count)];
        // All pool names taken — generate numbered fallback
        for (int k = 0; k < 10000; k++) {
            string fallback = "mouse" + k;
            if (!usedNames.Contains(fallback)) return fallback;
        }
        return "mouse" + Rng.Range(10000, 99999);
    }

    void LoadItemIcons() {
        Sprite fallback = Resources.Load<Sprite>("Sprites/Items/split/default/icon");
        if (fallback == null) Debug.LogError("Db: missing default item icon at Sprites/Items/split/default/icon");
        // All Book-class items share one sprite — no per-book artwork. Load once.
        Sprite booksSprite = Resources.Load<Sprite>("Sprites/Items/split/books/icon");
        if (booksSprite == null) Debug.LogWarning("Db: missing books sprite at Sprites/Items/split/books/icon — books will use default icon");
        foreach (Item item in itemsFlat) {
            Sprite loaded;
            if (item.itemClass == ItemClass.Book) {
                loaded = booksSprite;
            } else {
                string iName = item.name.Trim().Replace(" ", "");
                loaded = Resources.Load<Sprite>($"Sprites/Items/split/{iName}/icon");
            }
            if (loaded != null) {
                item.icon = loaded;
            } else if (item.children == null) {
                // Leaf items always get at least the fallback icon.
                item.icon = fallback;
            }
            // Group items with no dedicated sprite stay null — ItemIcon resolves
            // them dynamically to the most-owned child leaf at display time.
        }
    }

    // Runtime-generates one book item per research tech, starting at ID 301.
    // Called from Awake() after ReadJson (so Item loading is complete) but before
    // itemsFlat is trimmed (line 74) — AddItemToDb relies on the untrimmed array.
    // The fiction book (id 300) is hand-authored in itemsDb.json; tech books get
    // sequential IDs so this scales automatically as new techs are added.
    // Mapping tech.id → book item.id is stored in bookItemIdByTechId for the
    // scientist study task (M5) to look up which book aids a given tech.
    // Re-parses researchDb.json here because ResearchSystem.Awake hasn't run yet;
    // the duplicated parse is tiny (small file, startup-only).
    void GenerateBookItems() {
        // Static fields persist across scene reloads even though the Db instance is recreated;
        // clear so we don't accumulate stale mappings on a second Awake.
        bookItemIdByTechId.Clear();
        _cachedTechs = null;
        if (!itemByName.TryGetValue("book", out Item bookGroup)) {
            Debug.LogError("Db: 'book' group missing from itemsDb.json — skipping tech-book generation");
            return;
        }
        string researchJson = LoadJsonText("researchDb");
        if (researchJson == null) return; // already logged
        try {
            _cachedTechs = JsonConvert.DeserializeObject<ResearchNodeData[]>(researchJson);
        } catch (Exception e) {
            Debug.LogError($"Db: failed to parse researchDb.json for book generation: {e.Message}");
            return;
        }
        // Tech book IDs start at 302: 300=book group, 301=fiction_book child (in JSON).
        var newChildren = new List<Item>();
        int nextId = 302;
        foreach (ResearchNodeData tech in _cachedTechs) {
            if (tech == null || string.IsNullOrEmpty(tech.name)) continue;
            var book = new Item {
                id       = nextId,
                name     = $"book_{tech.name.ToLower().Replace(' ', '_')}",
                decayRate = 0f,
                discrete = true,
                itemClass = ItemClass.Book,
                children = null,
                parent   = bookGroup,
            };
            AddItemToDb(book);
            // AddItemToDb only runs the parent→child inheritance loop when traversing children
            // declared in JSON. Since these are runtime-attached children, replicate the same
            // field-inheritance the JSON-load path would have applied.
            if (book.decayRate == 0f) book.decayRate = bookGroup.decayRate;
            if (!book.discrete) book.discrete = bookGroup.discrete;
            if (book.itemClass == ItemClass.Default) book.itemClass = bookGroup.itemClass;
            newChildren.Add(book);
            bookItemIdByTechId[tech.id] = nextId;
            nextId++;
        }
        // Append the new tech books to the book group's children array so the inventory tree
        // (StoragePanel, global panel, etc.) renders them under "book" alongside fiction_book.
        if (newChildren.Count > 0) {
            Item[] existing = bookGroup.children ?? Array.Empty<Item>();
            var combined = new Item[existing.Length + newChildren.Count];
            existing.CopyTo(combined, 0);
            for (int i = 0; i < newChildren.Count; i++) combined[existing.Length + i] = newChildren[i];
            bookGroup.children = combined;
        }
    }

    // Runtime-generates one scribe recipe per tech that has a book item (from GenerateBookItems).
    // Each recipe: 1 paper in, 1 tech-book out, crafted at the scriptorium by a scribe. The
    // recipe is wired to be gated by its own tech via ResearchSystem.InjectBookRecipeUnlocks
    // (which reads bookRecipeIdByTechId after Db.Awake completes).
    // Must run after ReadJson (so recipes[] and jobs[] exist) and after GenerateBookItems
    // (so book Item objects are registered and looked up by id).
    void GenerateBookRecipes() {
        bookRecipeIdByTechId.Clear();
        if (_cachedTechs == null) {
            Debug.LogError("Db: no cached techs from GenerateBookItems — skipping book recipe generation");
            return;
        }
        if (!jobByName.TryGetValue("scribe", out Job scribe)) {
            Debug.LogError("Db: 'scribe' job missing from jobsDb.json — skipping book recipe generation");
            return;
        }
        if (!itemByName.ContainsKey("paper")) {
            Debug.LogError("Db: 'paper' item missing from itemsDb.json — skipping book recipe generation");
            return;
        }
        Item paper = itemByName["paper"];
        int nextId = 200;
        foreach (ResearchNodeData tech in _cachedTechs) {
            if (tech == null) continue;
            if (!bookItemIdByTechId.TryGetValue(tech.id, out int bookItemId)) continue;
            // Skip recipe id conflicts with any hand-authored recipe.
            while (nextId < recipes.Length && recipes[nextId] != null) nextId++;
            if (nextId >= recipes.Length) {
                Debug.LogError($"Db: ran out of recipe ids for book recipes (started at 200, hit {nextId})");
                return;
            }
            Item bookItem = items[bookItemId];
            var recipe = new Recipe {
                id               = nextId,
                job              = "scribe",
                tile             = "scriptorium",
                description      = $"write the {tech.name} book",
                workload         = 20f,
                maxRoundsPerTask = 1, // one book per trip — see Recipe.maxRoundsPerTask
                inputs           = new[] { new ItemQuantity(paper,    ItemStack.LiangToFen(1f)) },
                outputs          = new[] { new ItemQuantity(bookItem, ItemStack.LiangToFen(1f)) },
                // ninputs/noutputs aren't consumed at runtime (those are the JSON staging fields;
                // inputs/outputs are what the task layer uses), but keep them non-null so any
                // code that enumerates them — e.g. a future recipe-panel introspection — doesn't NRE.
                ninputs  = new ItemNameQuantity[0],
                noutputs = new ItemNameQuantity[0],
            };
            recipes[nextId] = recipe;
            if (scribe.nRecipes >= Job.maxRecipes) {
                Debug.LogError($"Db: scribe job at max recipe capacity ({Job.maxRecipes}) — dropping {recipe.description}");
                recipes[nextId] = null;
                return;
            }
            scribe.recipes[scribe.nRecipes++] = recipe;
            bookRecipeIdByTechId[tech.id] = nextId;
            nextId++;
        }
    }

    // Recipe panel names truncate in the card header when too long. Warn at load for any
    // description longer than this reference string so over-long names get noticed and trimmed.
    void WarnLongRecipeNames() {
        const string reference = "smelt malachite into copper (wood-";
        foreach (Recipe r in recipes) {
            if (r == null || string.IsNullOrEmpty(r.description)) continue;
            if (r.description.Length > reference.Length)
                Debug.LogWarning($"Recipe name too long ({r.description.Length} > {reference.Length}): \"{r.description}\" (id {r.id}). Shorten it — long names truncate in the Recipes panel.");
        }
    }

    // Validates that no group/parent items appear as recipe outputs, plant products, or tile drops.
    // Group items (those with children) are only valid as recipe *inputs* (where they act as wildcards).
    void ValidateNoGroupOutputs(){
        foreach (Recipe recipe in recipes){
            if (recipe == null) continue;
            foreach (ItemQuantity iq in recipe.outputs)
                if (iq.item.children != null)
                    Debug.LogError($"Db validation: recipe '{recipe.description}' output '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
        foreach (PlantType pt in plantTypes){
            if (pt == null || pt.products == null) continue;
            foreach (ItemQuantity iq in pt.products)
                if (iq.item.children != null)
                    Debug.LogError($"Db validation: plant '{pt.name}' product '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
        foreach (TileType tt in tileTypes){
            if (tt == null || tt.products == null) continue;
            foreach (ItemQuantity iq in tt.products)
                if (iq.item.children != null)
                    Debug.LogError($"Db validation: tile '{tt.name}' product '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
        foreach (List<ProcessorRecipe> list in processorRecipesByBuilding.Values){
            foreach (ProcessorRecipe pr in list)
                foreach (ItemQuantity iq in pr.outputs)
                    if (iq.item.children != null)
                        Debug.LogError($"Db validation: processor recipe '{pr.description}' output '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
    }

    // Cross-checks processor recipes against buildings: every recipe must target a real
    // building, and every hasProcessor building must have at least one recipe — otherwise
    // its Processor component can't be created (see Building constructor).
    void ValidateProcessorRecipes(){
        foreach (string buildingName in processorRecipesByBuilding.Keys)
            if (!structTypeByName.ContainsKey(buildingName))
                Debug.LogError($"Db validation: processor recipe targets building '{buildingName}' which does not exist.");
        foreach (StructType st in structTypes){
            if (st == null || !st.hasProcessor) continue;
            if (GetProcessorRecipe(st.name) == null)
                Debug.LogError($"Db validation: building '{st.name}' has hasProcessor=true but no processor recipe in processorRecipesDb.json.");
        }
    }

    // Returns the processor recipe a building runs, or null if none is declared.
    // One building = one recipe today; multi-recipe selection is a future extension,
    // at which point callers pick from processorRecipesByBuilding[name] directly.
    public static ProcessorRecipe GetProcessorRecipe(string buildingName){
        if (processorRecipesByBuilding != null
            && processorRecipesByBuilding.TryGetValue(buildingName, out List<ProcessorRecipe> list)
            && list.Count > 0)
            return list[0];
        return null;
    }

    // Warns if a discrete item's unit is too heavy to fit in even the largest storage stack.
    // Such an item would be un-storable — every stack's EffectiveCapacity floors to 0 — yet
    // CalculateWorkPossible would still let one un-storable unit get crafted onto the floor.
    // A content-authoring sanity net for heavy discrete items (stools, statues).
    void ValidateDiscreteUnitsFit(){
        int maxStorageStack = 0;
        foreach (StructType st in structTypes)
            if (st != null && st.storageStackSize > maxStorageStack)
                maxStorageStack = st.storageStackSize; // already in fen (StructType.OnDeserialized)
        if (maxStorageStack == 0) return; // no storage buildings defined
        foreach (Item item in itemsFlat){
            if (item == null || !item.discrete) continue;
            if (item.unitFen > maxStorageStack)
                Debug.LogError($"Db validation: discrete item '{item.name}' has unitWeight {item.unitWeight} " +
                    $"({item.unitFen} fen) — heavier than the largest storage stack ({maxStorageStack} fen). " +
                    $"It will be un-storable.");
        }
    }

    // Loads a JSON file from Assets/Resources/. Use this instead of
    // File.ReadAllText(Application.dataPath + "/Resources/X.json") — that pattern
    // works in the Editor but breaks in built players, where Resources/ is baked
    // into a binary blob (not a real folder on disk). Returns null and logs on
    // miss; callers should bail rather than try to recover.
    // DTO for buildingEdgeMasks.json (baked by BuildingEdgeMaskBaker). Per-building bitmasks
    // (bit dy*nx+dx) of which footprint tiles have a solid left / right edge.
    class EdgeMaskEntry { public string name; public int left; public int right; }

    static string LoadJsonText(string resourceName) {
        TextAsset ta = Resources.Load<TextAsset>(resourceName);
        if (ta == null) {
            Debug.LogError($"Db: Resources/{resourceName}.json not found");
            return null;
        }
        return ta.text;
    }

    void ReadJson(){
        // read Items
        string jsonTextItems = LoadJsonText("itemsDb");
        if (jsonTextItems == null) return;
        foreach (Item item in JsonConvert.DeserializeObject<Item[]>(jsonTextItems))
            AddItemToDb(item);

        // read Tiles
        string jsonTileTypes = LoadJsonText("tilesDb");
        if (jsonTileTypes == null) return;
        TileType[] tileTypesUnplaced = JsonConvert.DeserializeObject<TileType[]>(jsonTileTypes);
        foreach (TileType tileType in tileTypesUnplaced){
            if (tileTypes[tileType.id] != null){Debug.LogError($"multiple tile types with id={tileType.id}: '{tileTypes[tileType.id].name}' and '{tileType.name}'");}
            tileTypes[tileType.id] = tileType;
            tileTypeByName.Add(tileType.name, tileType);
        }

        // read Jobs
        string jsonTextJobs = LoadJsonText("jobsDb");
        if (jsonTextJobs == null) return;
        Job[] jobsUnplaced = JsonConvert.DeserializeObject<Job[]>(jsonTextJobs);
        foreach (Job job in jobsUnplaced){
            if (jobs[job.id] != null){Debug.LogError($"multiple jobs with id={job.id}: '{jobs[job.id].name}' and '{job.name}'");}
            jobs[job.id] = job;
            if (job.name != null){
                jobByName.Add(job.name, job);
            }
        }

        // Resolve skill weight string keys to Skill enum values for fast runtime lookup.
        foreach (Job job in jobsUnplaced) {
            if (job.skillWeights == null) continue;
            job.resolvedSkillWeights = new Dictionary<Skill, float>();
            foreach (var kv in job.skillWeights) {
                if (System.Enum.TryParse<Skill>(kv.Key, true, out Skill s))
                    job.resolvedSkillWeights[s] = kv.Value;
                else
                    Debug.LogWarning($"Job '{job.name}': unknown skill '{kv.Key}' in skillWeights");
            }
        }

        // read Structures
        string jsonStructTypes = LoadJsonText("buildingsDb");
        if (jsonStructTypes == null) return;
        StructType[] structTypesUnplaced = JsonConvert.DeserializeObject<StructType[]>(jsonStructTypes);
        foreach (StructType structType in structTypesUnplaced){
            if (structTypes[structType.id] != null){Debug.LogError($"multiple struct types with id={structType.id}: '{structTypes[structType.id].name}' and '{structType.name}'");}
            structType.isPlant = false;
            structTypes[structType.id] = structType;
            structTypeByName.Add(structType.name, structType);
        }

        // Baked side-ladder edge masks (Tools/Bake Building Edge Masks). Optional — loaded
        // directly (not via LoadJsonText) so an un-baked project doesn't log an error; absent
        // file leaves masks unset and SideEdgeSolid stays permissive.
        TextAsset edgeMaskAsset = Resources.Load<TextAsset>("buildingEdgeMasks");
        if (edgeMaskAsset != null) {
            EdgeMaskEntry[] masks = JsonConvert.DeserializeObject<EdgeMaskEntry[]>(edgeMaskAsset.text);
            if (masks != null)
                foreach (EdgeMaskEntry m in masks)
                    if (m != null && m.name != null && structTypeByName.TryGetValue(m.name, out StructType est))
                        est.SetEdgeMasks(m.left, m.right);
        }

        string jsonPlantTypes = LoadJsonText("plantsDb");
        if (jsonPlantTypes == null) return;
        PlantType[] plantTypesUnplaced = JsonConvert.DeserializeObject<PlantType[]>(jsonPlantTypes);
        foreach (PlantType plantType in plantTypesUnplaced){
            if (plantTypes[plantType.id] != null){Debug.LogError($"multiple plant types with id={plantType.id}: '{plantTypes[plantType.id].name}' and '{plantType.name}'");}
            plantTypes[plantType.id] = plantType;
            plantTypeByName.Add(plantType.name, plantType);
            // also add each plant as a building so you can build it
            if (structTypes[plantType.id] != null){Debug.LogError($"plant '{plantType.name}' id={plantType.id} collides with existing struct type '{structTypes[plantType.id].name}'");}
            structTypes[plantType.id] = plantType;
            plantType.isPlant = true;
            structTypeByName.Add(plantType.name, plantType);
        } 


        // read Recipes
        string jsonTextRecipes = LoadJsonText("recipesDb");
        if (jsonTextRecipes == null) return;
        Recipe[] recipesUnplaced = JsonConvert.DeserializeObject<Recipe[]>(jsonTextRecipes);
        foreach (Recipe recipe in recipesUnplaced){
            if (recipes[recipe.id] != null){Debug.LogError($"multiple recipes with id={recipe.id}: '{recipes[recipe.id].description}' and '{recipe.description}'");}
            recipes[recipe.id] = recipe;
            if (jobByName.ContainsKey(recipe.job)){ // add recipe to job's array of recipes
                Job job = jobByName[recipe.job];
                job.recipes[job.nRecipes] = recipe;
                job.nRecipes += 1;
            }
        }

        // Propagate each job's defaultSkill to any recipe that didn't specify its own skill.
        foreach (Recipe recipe in recipesUnplaced) {
            if (recipe.skill != null) continue;
            if (jobByName.TryGetValue(recipe.job, out Job j))
                recipe.skill = j.defaultSkill;
        }

        // read Processor recipes (passive timed conversions — see Processor.cs). Must load
        // after itemsDb: ProcessorRecipe.OnDeserialized resolves item names to fen quantities.
        // Bucketed by building name; Building resolves its recipe via Db.GetProcessorRecipe.
        // Missing file is non-fatal — ValidateProcessorRecipes then flags any orphaned
        // hasProcessor building.
        string jsonProcRecipes = LoadJsonText("processorRecipesDb");
        if (jsonProcRecipes != null) {
            var seenProcIds = new HashSet<int>();
            foreach (ProcessorRecipe pr in JsonConvert.DeserializeObject<ProcessorRecipe[]>(jsonProcRecipes)) {
                if (!seenProcIds.Add(pr.id))
                    Debug.LogError($"Db: duplicate processor recipe id={pr.id} ('{pr.description}')");
                if (!processorRecipesByBuilding.TryGetValue(pr.building, out List<ProcessorRecipe> list)) {
                    list = new List<ProcessorRecipe>();
                    processorRecipesByBuilding[pr.building] = list;
                }
                list.Add(pr);
            }
        }

        // read Flowers (purely decorative — no Structure registration, no save state).
        // Missing file is non-fatal: log and continue. The game runs fine without any
        // flowers; FlowerController just has nothing to spawn.
        string jsonFlowerTypes = LoadJsonText("flowersDb");
        if (jsonFlowerTypes != null) {
            FlowerType[] flowerTypesUnplaced = JsonConvert.DeserializeObject<FlowerType[]>(jsonFlowerTypes);
            foreach (FlowerType ft in flowerTypesUnplaced) {
                if (flowerTypeByName.ContainsKey(ft.name)) {
                    Debug.LogError($"multiple flower types with name='{ft.name}'");
                    continue;
                }
                if (flowerTypesCount >= flowerTypes.Length) {
                    Debug.LogError($"flower types exceed capacity ({flowerTypes.Length}) — increase Db.flowerTypes array size");
                    break;
                }
                flowerTypes[flowerTypesCount++] = ft;
                flowerTypeByName.Add(ft.name, ft);
            }
        }
    }

    public static Job GetJobByName(string name) {
        jobByName.TryGetValue(name, out Job job);
        return job;
    }

    void AddItemToDb(Item item) {
        if (items[item.id] != null)        Debug.LogError($"multiple items with id={item.id}: '{items[item.id].name}' and '{item.name}'");
        if (itemByName.ContainsKey(item.name)) Debug.LogError($"multiple items with name='{item.name}' (existing id={itemByName[item.name].id}, new id={item.id})");
        items[item.id] = item;
        if (item.name != null) {
            itemByName.Add(item.name, item);
            iidByName.Add(item.name, item.id);
            itemsFlat[itemsCount++] = item;
        }
        if (item.children != null) {
            foreach (Item child in item.children) {
                AddItemToDb(child);
                child.parent = item;
                if (child.decayRate == 0f) child.decayRate = item.decayRate;
                if (!child.discrete) child.discrete = item.discrete;
                if (child.unitWeight == 0f) child.unitWeight = item.unitWeight;
                if (child.itemClass == ItemClass.Default) child.itemClass = item.itemClass;
                // Furnishing fields cascade so authors can tag a single group (e.g. "cloth")
                // and every leaf descendant becomes a valid furnishing without per-leaf JSON.
                if (child.furnishingSlot == null)     child.furnishingSlot = item.furnishingSlot;
                if (child.furnishingHappiness == 0f)  child.furnishingHappiness = item.furnishingHappiness;
                if (child.furnishingLifetimeDays == 0f) child.furnishingLifetimeDays = item.furnishingLifetimeDays;
                if (child.furnishingSprite == null)   child.furnishingSprite = item.furnishingSprite;
                if (child.furnishingCost == 0f)       child.furnishingCost = item.furnishingCost;
            }
        }
    }
}

// A Job is the work specialization an Animal is assigned to (hauler, cook, scribe, etc.).
// Two distinct usages of the Job type, both indexed via Db.jobByName:
//   1. Animal.job — the animal's currently-assigned specialization. Determines what tasks
//      they can take: WOM canDo predicates compare `animal.job` to either the recipe's job
//      (for craft orders, via job.recipes[]) or to a structure's logistics job
//      (for SupplyBlueprint/Construct/Deconstruct — see StructType.job).
//   2. StructType.job (resolved from `njob`) — the LOGISTICS job for that structure,
//      i.e. who builds/supplies/deconstructs it. NOT who operates it. See Structure.cs for
//      detailed comment on njob/job and the operator-vs-logistics distinction.
// `recipes[]` is populated at recipe-load time: every recipe with `recipe.job == this.name`
// is appended here, so a job carries the list of recipes its animals can perform.
public class Job {
    public int id {get; set;} // set must be public for JSON deserialization
    public string name {get; set;}
    public string jobType {get; set;}
    public string defaultSkill {get; set;} // optional; skill domain used for recipes of this job (e.g. "woodworking")
    public bool defaultLocked {get; set;} // true = hidden from jobs panel until unlocked
    // Optional one-way building gate: building type name whose first construction permanently
    // reveals this job (e.g. "sawmill" → woodworker). Independent of the tech gate — a
    // defaultLocked job is unlocked by EITHER its gating tech OR this building being built.
    // One-way: demolishing the building does not re-hide the job. null = no building gate.
    public string unlockedByBuilding {get; set;}
    public int nRecipes = 0;
    public static int maxRecipes = 100;
    public Recipe[] recipes = new Recipe[maxRecipes]; // max 100 recipes per job

    // Job-skill affinity weights for automatic job swapping (e.g. farmer → farming:0.8, construction:0.2).
    // Authored in JSON as string keys; resolved to Skill enum at load time.
    public Dictionary<string, float> skillWeights {get; set;}
    [JsonIgnore] public Dictionary<Skill, float> resolvedSkillWeights;
}

public class Recipe {
    public int id {get; set;}
    public string job {get; set;}
    public string description {get; set;} // optional (maybe make the getter return something other than null?)
    public string tile {get; set;} // actually is a tile or a building...
    public float workload {get; set;}
    public string research   { get; set; }   // optional: research name to advance per cycle
    public float  researchPoints { get; set; }  // progress added to that research per cycle
    public string skill      { get; set; }   // optional: overrides job.defaultSkill for this recipe (e.g. "mining")
    // Caps how many rounds a single CraftTask may queue up for this recipe. 0 (default) = no cap;
    // CalculateWorkPossible's normal estimate is used. Set to 1 for "one item per trip" recipes
    // like book-writing where each cycle should be a deliberate, discrete action — not a batch.
    public int    maxRoundsPerTask { get; set; }
    public bool   hidden { get; set; } // true = omit from the Recipes panel (e.g. dig/mine pseudo-recipes that aren't conventional crafting)
    public TileType tileType;
    public ItemNameQuantity[] ninputs {get; set;}
    public ItemNameQuantity[] noutputs {get; set;}
    public ItemQuantity[] inputs;
    public ItemQuantity[] outputs;
    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        inputs = new ItemQuantity[ninputs.Length];
        outputs = new ItemQuantity[noutputs.Length];
        for (int i = 0; i < ninputs.Length; i++){
            inputs[i] = new ItemQuantity(ninputs[i]);
        }
        for (int i = 0; i < noutputs.Length; i++){
            outputs[i] = new ItemQuantity(noutputs[i]); // chance carried by the constructor
        }
    }
    // Economic desirability of running this recipe, from global stock vs per-item targets only
    // (nothing about recipe ratios). Each item contributes ratio = qty/target; a recipe is
    // favoured when inputs are abundant (ratio > 1) and outputs are scarce (ratio < 1).
    //
    // Inputs and outputs are combined as separate GEOMETRIC MEANS — GM(inputs) / GM(outputs) —
    // rather than one big product. The geomean is the natural average for multiplicative ratios
    // (arithmetic mean in log-space), and crucially it normalises by item count: a 3-input and a
    // 1-input recipe with the same per-item ratios score identically, so complex recipes aren't
    // penalised by raw product compounding (3 inputs at 0.5 would otherwise collapse to 0.125).
    //
    // NaN-free by construction (a NaN here once soft-locked crafting — see the urgency-Infinity
    // trap): a never-produced output (gmOut == 0) yields the +Infinity "produce this NOW" signal,
    // and an empty input (gmIn == 0) yields 0, with gmIn checked first so 0/0 never arises.
    public float Score(Dictionary<int, int> targets){
        if (targets == null) return 1;
        float gmIn  = GeoMeanRatio(inputs, targets);
        float gmOut = GeoMeanRatio(outputs, targets);
        if (gmIn == 0f)  return 0f;                       // an input is empty — recipe unmakeable
        if (gmOut == 0f) return float.PositiveInfinity;   // a never-produced output — make it now
        return gmIn / gmOut;
    }

    // Geometric mean of qty/target over the tracked, non-zero-target items in `list`.
    // Untracked ids and target==0 items are skipped (neutral — matches AllOutputsSatisfied);
    // with no scored items the mean is 1 (neutral), so an unscored side doesn't shift Score.
    private static float GeoMeanRatio(ItemQuantity[] list, Dictionary<int, int> targets){
        float product = 1f;
        int n = 0;
        foreach (ItemQuantity iq in list){
            if (!targets.TryGetValue(iq.item.id, out int target)) continue; // untracked — skip
            if (target == 0) continue;                                      // no target — neutral
            product *= (float)GlobalInventory.instance.Quantity(iq.item) / target;
            n++;
        }
        if (n == 0) return 1f;
        return UnityEngine.Mathf.Pow(product, 1f / n);
    }

    // Centralised gate for "can an animal currently pick / continue this recipe?":
    // player has it enabled in RecipePanel AND its tech is currently unlocked.
    // Every recipe-selection site (Animal.PickRecipe*, Animal.ChooseCraftTask) and the
    // mid-craft check in AnimalStateManager must go through this — keeping the gates
    // in one place prevents drift (past bug: ChooseCraftTask missed IsRecipeUnlocked
    // and mice walked to locked presses before failing on arrival).
    // Null-safe against early-startup / tests where the singletons aren't up yet.
    public bool IsEligibleForPicking() {
        if (RecipePanel.instance != null && !RecipePanel.instance.IsAllowed(id)) return false;
        if (ResearchSystem.instance != null && !ResearchSystem.instance.IsRecipeUnlocked(id)) return false;
        return true;
    }

    // Returns true if every tracked output is already at or above its target,
    // meaning this recipe should not be chosen (production is unneeded).
    // target=0 means "produce none" — any quantity ≥ 0 satisfies it.
    // Items missing from the targets dict are skipped as a safe fallback.
    public bool AllOutputsSatisfied(Dictionary<int, int> targets) => AllItemsSatisfied(outputs, targets);

    // Same satisfaction check, generalized over an arbitrary item list — used by Plant
    // harvest gating (products array) and recipe output gating (outputs array). See
    // AllOutputsSatisfied above for the semantics this preserves.
    public static bool AllItemsSatisfied(ItemQuantity[] items, Dictionary<int, int> targets) {
        if (targets == null || items == null) return false;
        bool anyTracked = false;
        foreach (var iq in items) {
            if (!targets.TryGetValue(iq.item.id, out int target)) continue;
            anyTracked = true;
            if (GlobalInventory.instance.Quantity(iq.item) < target) return false;
        }
        return anyTracked;
    }

    // ── Crafting batch sizing ──────────────────────────────────────────
    // How many rounds' worth of an output must be missing from the global
    // stockpile before a mouse may ignore the storage cap and batch-craft onto
    // the workshop floor. See Animal.CalculateWorkPossible.
    public const int ScarcityRoundsThreshold = 3;

    // Caps `rounds` so a craft session doesn't overshoot the player's per-output
    // target. Outputs already at/above their target are left alone — collateral
    // overshoot is accepted because AllOutputsSatisfied would have gated the recipe
    // out entirely if *every* output were satisfied. Ceiling division guarantees at
    // least enough rounds to cross the target, bounding overshoot to one round's
    // worth. Untracked outputs and a null targets dict impose no cap.
    public int CapRoundsByTarget(int rounds, Dictionary<int, int> targets){
        if (targets == null) return rounds;
        foreach (ItemQuantity output in outputs){
            if (!targets.TryGetValue(output.item.id, out int target)) continue;
            int headroom = target - GlobalInventory.instance.Quantity(output.item);
            if (headroom <= 0) continue;
            int roundsToTarget = (headroom + output.quantity - 1) / output.quantity;
            if (roundsToTarget < rounds) rounds = roundsToTarget;
        }
        return rounds;
    }

    // True when every output is scarce enough to justify a mouse ignoring the
    // storage cap and batch-crafting onto the workshop floor: global quantity below
    // both ScarcityRoundsThreshold rounds' worth AND the player-set target (so a
    // low/zero target clamps the bypass and prevents a floor flood). A null targets
    // dict uses the rounds threshold alone. Empty-output recipes are vacuously scarce.
    public bool AllOutputsScarce(Dictionary<int, int> targets){
        foreach (ItemQuantity output in outputs){
            int threshold = ScarcityRoundsThreshold * output.quantity;
            if (targets != null && targets.TryGetValue(output.item.id, out int target))
                threshold = System.Math.Min(threshold, target);
            if (GlobalInventory.instance.Quantity(output.item) >= threshold) return false;
        }
        return true;
    }
}

// A passive timed conversion run by a building's Processor component (see Processor.cs).
// Loaded from processorRecipesDb.json and linked to a building by name — mirroring how
// Recipe.tile links a craft recipe to its workstation. Distinct from Recipe because a
// processor recipe is a different concept: no active labor (processDays is wall-clock,
// not work-ticks), an optional temperature ramp, and no scoring / job-skill model.
// Quantities are authored in liang (float) and resolved to fen (int) in OnDeserialized.
public class ProcessorRecipe {
    public int id {get; set;}              // informational — recipes are keyed by building name, not array-indexed
    public string building {get; set;}     // name of the building whose Processor runs this recipe
    public string description {get; set;}
    public ItemNameQuantity[] ninputs {get; set;}   // raw JSON
    public ItemNameQuantity[] noutputs {get; set;}  // raw JSON
    public ItemQuantity[] inputs;                   // resolved from ninputs (liang → fen)
    public ItemQuantity[] outputs;                  // resolved from noutputs (liang → fen)
    public float processDays {get; set;}            // base duration at full (rate 1.0) speed
    public float? processTempMin {get; set;}        // null = constant rate (not temperature-scaled)
    public float? processTempIdeal {get; set;}
    public bool autoTap {get; set;}                 // schema stub — manual tap only for now
    // Optional tint for the building's _w liquid zone while the processor is Working
    // (e.g. the cloudy white of rice mash mid-fermentation). Authored as #RRGGBB; absent →
    // the zone keeps its loading colour. Parsed to processColor in OnDeserialized.
    public string processColorHex {get; set;}
    public Color32 processColor;                    // parsed; alpha=0 when processColorHex is unset

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context){
        int ni = ninputs?.Length ?? 0;
        inputs = new ItemQuantity[ni];
        for (int i = 0; i < ni; i++) inputs[i] = new ItemQuantity(ninputs[i]);
        int no = noutputs?.Length ?? 0;
        outputs = new ItemQuantity[no];
        for (int i = 0; i < no; i++) outputs[i] = new ItemQuantity(noutputs[i]);
        if (!string.IsNullOrEmpty(processColorHex)) {
            if (ColorUtility.TryParseHtmlString(processColorHex, out Color pc)) {
                processColor = pc;
                processColor.a = 255; // alpha flags "tint active" for the renderer
            } else {
                Debug.LogError($"ProcessorRecipe '{description}': invalid processColorHex '{processColorHex}'");
            }
        }
    }
}


public class HaulInfo {
    public Item item;
    public int quantity;
    public Tile itemTile;
    public Tile destTile; // floor consolidation destination (not storage — storage is on building.storage)
    public ItemStack itemStack;

    public HaulInfo(Item item, int quantity, Tile itemTile, Tile destTile, ItemStack itemStack) {
        this.item = item;
        this.quantity = quantity;
        this.itemTile = itemTile;
        this.destTile = destTile;
        this.itemStack = itemStack;
    }
}