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
    public static List<Item> tonicItems; // items with a buff effect (tonics); drink AI scans this
    // Edible items that are also a planting cost (some PlantType.costs entry). Mice avoid
    // eating the last few of these so farmers/loggers aren't left unable to replant — see
    // Animal.FindFood. Derived from plant data in LoadAll; no JSON flag.
    public static HashSet<Item> seedItems;
    public static List<Item> equipmentItems;
    public static List<Item> clothingItems;
    public static List<Item> hatItems; // leaf items under the "hat" group; worn in Animal.hatSlotInv
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
    // Processor recipes: ordinary `Recipe`s whose `tile` building hasProcessor, bucketed by
    // that building so the fill-time scorer can pick among them. They are NOT added to any
    // job.recipes (so the craft dispatch never runs them as CraftTasks) — the Processor's
    // Fill/Work/Tap orders drive them instead. See Recipe.isProcessorRecipe / GetProcessorRecipes.
    public static Dictionary<string, List<Recipe>> processorRecipesByBuilding;
    // Foundry recipes (foundryOp set). The foundry is a continuous melt pool, NOT a Processor:
    //   melt  — ore/bar → molten (auto, per deposited chunk, temperature-gated)
    //   alloy — molten + molten → molten (auto in the pool, gated by the cast target)
    //   cast  — molten → bars (by the foundry's cast target)
    // Bucketed here at load and kept OUT of both job.recipes and processorRecipesByBuilding, so
    // neither the craft dispatch nor the Processor orders ever run them. See SPEC-systems §Foundry.
    public static List<Recipe> foundryMeltRecipes;
    public static List<Recipe> foundryAlloyRecipes;
    public static List<Recipe> foundryCastRecipes;
    public static StructType[] structTypes = new StructType[600];
    public static PlantType[] plantTypes = new PlantType[600];
    public static TileType[] tileTypes = new TileType[100];
    // Flowers are decorative-only (no Structure registry, no save state). The flat
    // list is kept for FlowerController's weighted-pick loop; flowerTypeByName lets
    // future systems (e.g. a harvestability upgrade path) look up a type by name.
    public static FlowerType[] flowerTypes = new FlowerType[64];
    public static int flowerTypesCount = 0;

    // Mouse fur palette (cosmetic only). Each entry is the MAIN fur shade; the
    // Custom/Sprite shader reconstructs the highlight/shadow/eep shades by re-applying
    // their fixed offsets. Picked per-mouse by FurColorForSeed (weighted). Parallel lists:
    // furColorWeights[i] is the relative pick weight of furColors[i]. Loaded from furColors.json.
    public static List<Color> furColors = new List<Color>();
    public static List<float> furColorWeights = new List<float>();
    static float furColorTotalWeight = 0f;

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
        furColors.Clear();
        furColorWeights.Clear();
        furColorTotalWeight = 0f;
        bookRecipeIdByTechId = new Dictionary<int, int>();
        bookItemIdByTechId   = new Dictionary<int, int>();
        itemsByFurnishingSlot = new Dictionary<string, List<Item>>();
        processorRecipesByBuilding = new Dictionary<string, List<Recipe>>();
        foundryMeltRecipes  = new List<Recipe>();
        foundryAlloyRecipes = new List<Recipe>();
        foundryCastRecipes  = new List<Recipe>();
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
        tonicItems  = itemsFlat.Where(i => i.buffEffect.HasValue).ToList();
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
        hatItems = itemsFlat.Where(i => { Item cur = i; while (cur != null) { if (cur.name == "hat") return true; cur = cur.parent; } return false; }).ToList();
        // Resolve each job's preferredHat item name (authored in jobsDb.json) to the Item, now that
        // both jobs and items are loaded. Unknown name → logged error + left null (job seeks no hat).
        foreach (Job job in jobs) {
            if (job == null || string.IsNullOrEmpty(job.preferredHat)) continue;
            if (!itemByName.TryGetValue(job.preferredHat, out job.preferredHatItem))
                Debug.LogError($"Job '{job.name}': preferredHat '{job.preferredHat}' is not a known item");
        }
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
        if (!itemByName.TryGetValue("books", out Item bookGroup)) {
            Debug.LogError("Db: 'books' group missing from itemsDb.json — skipping tech-book generation");
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
        // Tech book IDs start at 302: 300=books group, 301=fiction_book child (in JSON).
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
        // (StoragePanel, global panel, etc.) renders them under "books" alongside fiction_book.
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
        if (!jobByName.ContainsKey("scribe")) {
            Debug.LogError("Db: 'scribe' job missing from jobsDb.json — skipping book recipe generation");
            return;
        }
        if (!itemByName.ContainsKey("paper")) {
            Debug.LogError("Db: 'paper' item missing from itemsDb.json — skipping book recipe generation");
            return;
        }
        Item paper = itemByName["paper"];
        // Labour-seconds to write one book (tended-processor duration). Long enough to be a project
        // spread across scribes/stints, NOT trapping one mouse. Tuning knob — playtest by feel.
        const float BookDuration = 120f;
        // Generated AFTER ReadJson, so the book recipes won't be auto-bucketed as processor recipes —
        // do it here. They live ONLY in processorRecipesByBuilding (never scribe.recipes) so the craft
        // dispatch never runs them; the scriptorium's Processor Fill/Work orders drive them instead.
        if (!processorRecipesByBuilding.TryGetValue("scriptorium", out List<Recipe> scriptoriumRecipes)) {
            scriptoriumRecipes = new List<Recipe>();
            processorRecipesByBuilding["scriptorium"] = scriptoriumRecipes;
        }
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
                id                = nextId,
                job               = "scribe",
                tile              = "scriptorium",
                description       = $"write {tech.name} book",
                duration          = BookDuration,  // processor recipe (tended): laboured over `duration`
                isProcessorRecipe = true,
                inputs            = new[] { new ItemQuantity(paper,    ItemStack.LiangToFen(1f)) },
                outputs           = new[] { new ItemQuantity(bookItem, ItemStack.LiangToFen(1f)) },
                // ninputs/noutputs aren't consumed at runtime (those are the JSON staging fields;
                // inputs/outputs are what the task layer uses), but keep them non-null so any
                // code that enumerates them — e.g. a future recipe-panel introspection — doesn't NRE.
                ninputs  = new ItemNameQuantity[0],
                noutputs = new ItemNameQuantity[0],
            };
            recipes[nextId] = recipe;
            scriptoriumRecipes.Add(recipe);
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
        foreach (List<Recipe> list in processorRecipesByBuilding.Values){
            foreach (Recipe pr in list)
                foreach (ItemQuantity iq in pr.outputs)
                    if (iq.item.children != null)
                        Debug.LogError($"Db validation: processor recipe '{pr.description}' output '{iq.item.name}' is a group item. Only leaf items may be produced.");
        }
    }

    // Cross-checks processor recipes against buildings: every hasProcessor building must have at
    // least one recipe (tile==buildingName) — otherwise its Processor has nothing to run.
    void ValidateProcessorRecipes(){
        foreach (StructType st in structTypes){
            if (st == null || !st.hasProcessor) continue;
            if (GetProcessorRecipes(st.name) == null)
                Debug.LogError($"Db validation: building '{st.name}' has hasProcessor=true but no recipe with tile=='{st.name}' in recipesDb.json.");
        }
    }

    // Returns the list of recipes a building's Processor can run (every recipe with tile==name),
    // or null if none. The fill-time scorer (Animal.PickProcessorRecipe) picks among them.
    public static List<Recipe> GetProcessorRecipes(string buildingName){
        if (processorRecipesByBuilding != null
            && processorRecipesByBuilding.TryGetValue(buildingName, out List<Recipe> list)
            && list.Count > 0)
            return list;
        return null;
    }

    // ── Foundry recipe lookups (foundryOp recipes; see ReadJson + SPEC-systems §Foundry) ──
    // The melt recipe for a meltable input item (ore or, later, a bar to remelt) → its molten
    // metal, or null if the item can't be melted. Matched on the recipe's single input leaf.
    public static Recipe GetFoundryMeltRecipe(Item input){
        if (input == null || foundryMeltRecipes == null) return null;
        foreach (Recipe r in foundryMeltRecipes)
            if (r.inputs.Length > 0 && r.inputs[0].item == input) return r;
        return null;
    }
    public static List<Recipe> GetFoundryAlloyRecipes() => foundryAlloyRecipes;
    public static List<Recipe> GetFoundryCastRecipes()  => foundryCastRecipes;

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

    // DTO for furColors.json — `hex` is the main fur shade (RRGGBB); `weight` is the
    // relative pick frequency (omitted/≤0 → treated as 1).
    class FurColorEntry { public string name; public string hex; public float weight; }

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
            // Foundry recipes (foundryOp set) are neither crafts nor processor batches — bucket them
            // into their own lists and skip both the job.recipes and processor paths below.
            if (recipe.foundryOp != null) {
                switch (recipe.foundryOp) {
                    case "melt":  foundryMeltRecipes.Add(recipe);  break;
                    case "alloy": foundryAlloyRecipes.Add(recipe); break;
                    case "cast":  foundryCastRecipes.Add(recipe);  break;
                    default: Debug.LogError($"Recipe {recipe.id} ('{recipe.description}'): unknown foundryOp '{recipe.foundryOp}' (expected melt|alloy|cast)."); break;
                }
                continue;
            }
            // A recipe with a duration is a batch conversion (processor), not a craft — keyed by
            // its `duration` field, NOT by the building, because one building can host both: the
            // brewery crafts yeast (workload) AND ferments rice wine (duration). Processor recipes
            // are bucketed by building for the fill-time scorer and kept OUT of job.recipes so the
            // craft dispatch never runs them as CraftTasks — the Fill/Work/Tap orders drive them.
            recipe.isProcessorRecipe = recipe.duration > 0f;
            if (recipe.isProcessorRecipe) {
                if (!processorRecipesByBuilding.TryGetValue(recipe.tile, out List<Recipe> procList)) {
                    procList = new List<Recipe>();
                    processorRecipesByBuilding[recipe.tile] = procList;
                }
                procList.Add(recipe);
            } else if (jobByName.ContainsKey(recipe.job)){ // add recipe to job's array of recipes
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

        // read fur colors (cosmetic mouse fur palette). Missing file is non-fatal —
        // FurColorForSeed falls back to the default gray shade, so mice render as today.
        string jsonFurColors = LoadJsonText("furColors");
        if (jsonFurColors != null) {
            foreach (FurColorEntry fc in JsonConvert.DeserializeObject<FurColorEntry[]>(jsonFurColors)) {
                if (ColorUtility.TryParseHtmlString("#" + fc.hex, out Color c)) {
                    float w = fc.weight > 0f ? fc.weight : 1f;
                    furColors.Add(c);
                    furColorWeights.Add(w);
                    furColorTotalWeight += w;
                } else {
                    Debug.LogError($"furColors: bad hex '{fc.hex}' for fur color '{fc.name}'");
                }
            }
        }
    }

    // The main fur shade (91989c) — the identity color for the Custom/Sprite remap, and
    // FurColorForSeed's fallback when the palette is empty (missing/failed furColors.json).
    public static readonly Color DefaultFurColor = new Color(145f / 255f, 152f / 255f, 156f / 255f, 1f);

    // Deterministic per-mouse fur color from its rngSeed. Hashes the seed directly rather
    // than drawing from Animal.random, so picking a color never perturbs the AI RNG stream;
    // stable across save/load since rngSeed is persisted. Caveat: reordering furColors.json
    // reshuffles existing mice's colors on next load — acceptable for a cosmetic.
    public static Color FurColorForSeed(int seed) {
        if (furColors.Count == 0) return DefaultFurColor;
        uint h = (uint)seed * 2654435761u; // Knuth multiplicative hash
        if (furColorTotalWeight <= 0f) return furColors[(int)(h % (uint)furColors.Count)];
        // Map the hash uniformly into [0, totalWeight) and walk the cumulative weights.
        double t = (h / 4294967296.0) * furColorTotalWeight; // h ∈ [0, 2^32) → [0, total)
        float acc = 0f;
        for (int i = 0; i < furColors.Count; i++) {
            acc += furColorWeights[i];
            if (t < acc) return furColors[i];
        }
        return furColors[furColors.Count - 1];
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
                if (child.fuelValue == 0f) child.fuelValue = item.fuelValue;
                if (!child.hidden) child.hidden = item.hidden;
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
    // Whether animals on this job actively seek out a tool to equip. Tools only speed up work that
    // routes through ModifierSystem.GetWorkMultiplier (gathering, crafting, research, construction),
    // so purely-logistical jobs (hauler, merchant, runner) gain nothing and shouldn't hunt for one.
    // A mouse reassigned off a tool-using job KEEPS any tool it already holds — this gate only stops
    // the active seek. Defaults true; set false in jobsDb.json for jobs with no tool benefit.
    public bool usesTools {get; set;} = true;
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

    // Optional: the hat this job's mice prefer to wear, for at-a-glance identification (e.g.
    // farmer → bamboo hat). Authored as an item name in jobsDb.json; resolved to the Item at
    // load. null = this job seeks no hat. Read by Animal.FindHat.
    public string preferredHat {get; set;}
    [JsonIgnore] public Item preferredHatItem;
}

public class Recipe {
    public int id {get; set;}
    public string job {get; set;}
    public string description {get; set;} // optional (maybe make the getter return something other than null?)
    public string tile {get; set;} // actually is a tile or a building...
    public float workload {get; set;}
    // Abstract fuel energy this recipe burns per round, satisfied by ANY item with fuelValue>0
    // (coal, wood, …) at potency-scaled quantity — not a specific fuel item. 0 = no fuel needed.
    // See SPEC-data §Fuel and GlobalInventory.CanCraft/PickFuel.
    public float fuelCost { get; set; }
    public string research   { get; set; }   // optional: research name a completed cycle advances
                                              // (amount derives from workload — see ResearchSystem.PassiveCraftRate)
    public string skill      { get; set; }   // optional: overrides job.defaultSkill for this recipe (e.g. "mining")
    // Caps how many rounds a single CraftTask may queue up for this recipe. 0 (default) = no cap;
    // CalculateWorkPossible's normal estimate is used. Set to 1 for "one item per trip" recipes
    // like book-writing where each cycle should be a deliberate, discrete action — not a batch.
    public int    maxRoundsPerTask { get; set; }
    public bool   hidden { get; set; } // true = omit from the Recipes panel (e.g. dig/mine pseudo-recipes that aren't conventional crafting)

    // ── Processor fields ────────────────────────────────────────────────────
    // Set only on recipes run by a building with a Processor (hasProcessor). They turn a plain
    // craft Recipe into a batch conversion: load inputs into a buffer, advance for `duration`
    // seconds, tap the whole batch out at once. `duration` is the canonical "seconds to complete
    // one batch": for an UNTENDED processor (brewery) it's elapsed in-game seconds, temperature-
    // scaled by the ramp below; for a TENDED one (cauldron) it's seconds of worker labour. Large
    // for slow ferments (rice wine 960 s = 2 in-game days); see Recipe.FormatDuration for display.
    public float  duration {get; set;}            // 0 on craft recipes; >0 on processor recipes
    public float? processTempMin {get; set;}      // null = constant rate (not temperature-scaled)
    public float? processTempIdeal {get; set;}
    public string processColorHex {get; set;}     // optional #RRGGBB tint for the Working-state liquid zone
    public Color32 processColor;                  // parsed; alpha=0 when processColorHex is unset
    // True when this recipe's `tile` building hasProcessor. Resolved at load (ReadJson). Such
    // recipes are bucketed in Db.processorRecipesByBuilding and kept OUT of job.recipes, so the
    // craft dispatch never picks them up — the Processor's Fill/Work/Tap orders run them instead.
    [JsonIgnore] public bool isProcessorRecipe;

    // ── Foundry fields ──────────────────────────────────────────────────────
    // Set only on foundry recipes (foundryOp != null). The foundry is a continuous melt pool, NOT a
    // Processor (see SPEC-systems §Foundry): melt (ore/bar → molten, auto per deposited chunk),
    // alloy (molten + molten → molten, auto in the pool), cast (molten → bars, by target). melt*
    // fields apply only to "melt" recipes; alloy/cast use ninputs/noutputs alone.
    public string foundryOp {get; set;}        // "melt" | "alloy" | "cast" | null (= a normal craft/processor recipe)
    public float  meltTempMin {get; set;}      // temperature where melt rate = 0; below it a chunk re-solidifies (rate goes negative)
    public float  meltTempIdeal {get; set;}    // temperature where melt rate = 1 (full speed)
    public float  meltDuration {get; set;}     // seconds to fully melt one chunk at ideal temp — INDEPENDENT of chunk size
    public float  meltHeatCost {get; set;}     // heat drawn from the pool per LIANG melted (latent heat; scales with chunk size)

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
        if (!string.IsNullOrEmpty(processColorHex)) {
            if (ColorUtility.TryParseHtmlString(processColorHex, out Color pc)) {
                processColor = pc;
                processColor.a = 255; // alpha flags "tint active" for the renderer
            } else {
                Debug.LogError($"Recipe {id} ('{description}'): invalid processColorHex '{processColorHex}'");
            }
        }
        // Migration guard: a recipe must not BOTH declare a fuelCost AND list a literal fuel
        // item (fuelValue>0, e.g. coal) in its inputs — that double-charges fuel. Catches a
        // half-finished retrofit. (Items load + cascade before recipes, so fuelValue is set.)
        if (fuelCost > 0f){
            foreach (ItemQuantity iq in inputs)
                if (iq.item != null && iq.item.fuelValue > 0f)
                    Debug.LogError($"Recipe {id} ('{description}') has fuelCost {fuelCost} AND fuel input '{iq.item.name}' (fuelValue {iq.item.fuelValue}) — double-charges fuel. Remove the fuel item from ninputs.");
        }
    }
    // Ratios above this are clamped to it. Stops one hugely-over-target item from dominating
    // the input geomean, and gives target==0 ("keep none") a finite "fully disposable" value
    // instead of dividing by zero.
    public const float MaxSurplusRatio = 20f;

    // How over-target a holding is, in [0, MaxSurplusRatio]. The shared scalar behind both
    // recipe scoring (below) and consumption leaf-selection (Task.ResolveConsumeLeaf).
    //   target == 0 → CAP if we hold any (fully disposable — consume/produce-against first), else 0
    //   target  > 0 → min(CAP, qty/target)
    // NaN-free: the target==0 branch means we never evaluate 0/0.
    public static float SurplusRatio(int qty, int target){
        if (target == 0) return qty > 0 ? MaxSurplusRatio : 0f;
        return UnityEngine.Mathf.Min(MaxSurplusRatio, qty / (float)target);
    }

    // Economic desirability of running this recipe, from global stock vs per-item targets only
    // (nothing about recipe ratios). Favoured when inputs are abundant and outputs are scarce:
    // Score = GM(input surplus) / GM(output scarcity).
    //
    // Each side is a separate GEOMETRIC MEAN rather than one big product. The geomean is the
    // natural average for multiplicative ratios (arithmetic mean in log-space), and normalises by
    // item count: a 3-input and a 1-input recipe with the same per-item ratios score identically,
    // so complex recipes aren't penalised by raw product compounding.
    //
    // NaN-free by construction (a NaN here once soft-locked crafting — see the urgency-Infinity
    // trap): a never-produced scarce output (gmOut == 0) yields the +Infinity "produce this NOW"
    // signal, and an empty input (gmIn == 0) yields 0, with gmIn checked first so 0/0 never arises.
    public float Score(Dictionary<int, int> targets){
        if (targets == null) return 1;
        float gmIn  = GeoMeanInputs(inputs, targets);
        float gmOut = GeoMeanOutputs(outputs, targets);
        if (gmIn == 0f)  return 0f;                       // an input is empty — recipe unmakeable
        if (gmOut == 0f) return float.PositiveInfinity;   // a never-produced output — make it now
        return gmIn / gmOut;
    }

    // Input side: abundance favours the recipe. Each input contributes its SurplusRatio (capped).
    // A GROUP input (wildcard, e.g. "wood") resolves to the MAX surplus over its leaf descendants
    // — the most over-target acceptable type — mirroring the leaf that Task.ResolveConsumeLeaf
    // will actually consume, so scoring and execution agree on per-leaf surplus. Untracked ids are
    // skipped (neutral); an all-empty group contributes 0 → gmIn 0 → recipe unmakeable.
    private static float GeoMeanInputs(ItemQuantity[] list, Dictionary<int, int> targets){
        float product = 1f;
        int n = 0;
        foreach (ItemQuantity iq in list){
            float ratio;
            if (iq.item.IsGroup){
                ratio = 0f;
                bool anyLeaf = false;
                foreach (Item leaf in iq.item.LeafDescendants()){
                    if (leaf.excludeFromGroupInput) continue; // never auto-substituted → exclude from scoring too
                    if (!targets.TryGetValue(leaf.id, out int lt)) continue;
                    anyLeaf = true;
                    ratio = UnityEngine.Mathf.Max(ratio, SurplusRatio(GlobalInventory.instance.Quantity(leaf), lt));
                }
                if (!anyLeaf) continue; // no tracked leaf — skip
            } else {
                if (!targets.TryGetValue(iq.item.id, out int t)) continue; // untracked — skip
                ratio = SurplusRatio(GlobalInventory.instance.Quantity(iq.item), t);
            }
            product *= ratio;
            n++;
        }
        if (n == 0) return 1f;
        return UnityEngine.Mathf.Pow(product, 1f / n);
    }

    // Output side: scarcity favours the recipe. Only SCARCE outputs (qty < target) count — a
    // surplus output (qty >= target) is skipped so an over-target byproduct (e.g. sawdust) can't
    // drag the score down and suppress a needed primary. target==0 → qty>=0 → always skipped
    // (subsumes the old "skip target==0" rule). Untracked ids skipped. Outputs are always leaves
    // (ValidateNoGroupOutputs). A scarce output at qty 0 gives ratio 0 → gmOut 0 → Score +Infinity.
    private static float GeoMeanOutputs(ItemQuantity[] list, Dictionary<int, int> targets){
        float product = 1f;
        int n = 0;
        foreach (ItemQuantity iq in list){
            if (!targets.TryGetValue(iq.item.id, out int target)) continue; // untracked — skip
            int qty = GlobalInventory.instance.Quantity(iq.item);
            if (qty >= target) continue;                                    // surplus — neutral
            product *= qty / (float)target;
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

    // Player-facing batch duration. Short batches (cauldron tonics, ~8 s of labour) read in
    // seconds; long ones (brewery ferments, hundreds of seconds of elapsed in-game time) read
    // in in-game days so "960" doesn't show as a wall of seconds. Threshold 60 s. Concise per
    // the UI style: "8s", "2 days", "1.5 days".
    public static string FormatDuration(float seconds) {
        float dayLen = World.ticksInDay > 0 ? World.ticksInDay : 480f;
        // Short processes (under half an in-game day) read more naturally in seconds than as a
        // fractional-day count, e.g. a 48s smelt vs "0.1 days".
        if (seconds < dayLen * 0.5f) return Mathf.RoundToInt(seconds) + "s";
        float days = seconds / dayLen;
        string n = days == Mathf.Floor(days) ? ((int)days).ToString() : days.ToString("0.#");
        return n + (days == 1f ? " day" : " days");
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