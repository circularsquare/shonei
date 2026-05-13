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
    public static int happinessMaxScore; // happinessNeeds.Count + 1 (housing) + 2 (temp max) + ceil(maxFurnishingPerMouse)
    // Max furnishing happiness an animal could get if they lived in the most generously
    // furnished house type with the highest-happiness item in every slot. Computed from
    // structTypes × itemsByFurnishingSlot at Db.LoadAll time. Drives the bar scale on the
    // GlobalHappinessPanel's furnishing row and contributes to happinessMaxScore.
    public static float maxFurnishingPerMouse;

    // Preferred display order for the happiness panel.
    // Food needs first, then decoration, then social, then leisure.
    // Needs not in this list are appended alphabetically at the end (future-proofing).
    private static readonly string[] happinessNeedsDisplayOrder = {
        "wheat", "fruit", "soymilk", "dairy",  // food
        "fountain",                    // decoration
        "social",                      // social
        "fireplace", "bench", "reading", // leisure
    };

    // Largest decorRadius across all structTypes. Computed at startup; used by Animal.ScanForNearbyDecorations.
    public static int maxDecoScanRadius;
    public static Job[] jobs = new Job[100];
    public static Recipe[] recipes = new Recipe[500];
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

        // Reset every static collection — without this, a scene reload (e.g. PlayMode
        // snapshot tests, or a future "new game" feature) finds the previous Db's
        // entries still populated and AddItemToDb fires duplicate-id errors.
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
        itemsFlat = itemsFlat.Take(itemsCount).ToArray();
        edibleItems = itemsFlat.Where(i => i.foodValue > 0).OrderByDescending(i => i.foodValue).ToList();
        equipmentItems = itemsFlat.Where(i => { Item cur = i; while (cur != null) { if (cur.name == "tools") return true; cur = cur.parent; } return false; }).ToList();
        clothingItems = itemsFlat.Where(i => { Item cur = i; while (cur != null) { if (cur.name == "clothing") return true; cur = cur.parent; } return false; }).ToList();
        BuildFurnishingSlotRegistry();
        BuildHappinessNeedRegistry();
        ValidateNoGroupOutputs();
        LoadItemIcons();
        LoadNames();
        Debug.Log("db loaded");
    }

    // Buckets every leaf item by its `furnishingSlot` so WOM dispatch and FurnishingSlots
    // can look up "what items fit a 'cloth' slot?" in O(1). Items inherit furnishingSlot
    // from their group parent (see AddItemToDb), so authors only tag the group.
    void BuildFurnishingSlotRegistry() {
        itemsByFurnishingSlot = new Dictionary<string, List<Item>>();
        foreach (Item item in itemsFlat) {
            if (item == null || item.IsGroup) continue; // only leaves can be installed
            if (string.IsNullOrEmpty(item.furnishingSlot)) continue;
            if (!itemsByFurnishingSlot.TryGetValue(item.furnishingSlot, out var list)) {
                list = new List<Item>();
                itemsByFurnishingSlot[item.furnishingSlot] = list;
            }
            list.Add(item);
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
        happinessMaxScore = happinessNeeds.Count + 1 + 2 + Mathf.CeilToInt(maxFurnishingPerMouse); // +1 housing, +2 temp max, + furnishing ceiling

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
    }

    // Loads a JSON file from Assets/Resources/. Use this instead of
    // File.ReadAllText(Application.dataPath + "/Resources/X.json") — that pattern
    // works in the Editor but breaks in built players, where Resources/ is baked
    // into a binary blob (not a real folder on disk). Returns null and logs on
    // miss; callers should bail rather than try to recover.
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
                if (child.itemClass == ItemClass.Default) child.itemClass = item.itemClass;
                // Furnishing fields cascade so authors can tag a single group (e.g. "cloth")
                // and every leaf descendant becomes a valid furnishing without per-leaf JSON.
                if (child.furnishingSlot == null)     child.furnishingSlot = item.furnishingSlot;
                if (child.furnishingHappiness == 0f)  child.furnishingHappiness = item.furnishingHappiness;
                if (child.furnishingLifetimeDays == 0f) child.furnishingLifetimeDays = item.furnishingLifetimeDays;
                if (child.furnishingSprite == null)   child.furnishingSprite = item.furnishingSprite;
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
    public bool defaultLocked {get; set;} // true = hidden from jobs panel until unlocked via research
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
            inputs[i] = new ItemQuantity(ninputs[i].name, ItemStack.LiangToFen(ninputs[i].quantity));
        }
        for (int i = 0; i < noutputs.Length; i++){
            outputs[i] = new ItemQuantity(noutputs[i].name, ItemStack.LiangToFen(noutputs[i].quantity));
            outputs[i].chance = noutputs[i].chance;
        }
    }
    public float Score(Dictionary<int, int> targets){ // only takes into account global quantity / target. nothing about recipe ratios.
        if (targets == null) return 1;
        float score = 1;
        foreach (ItemQuantity iq in inputs){
            if (!targets.TryGetValue(iq.item.id, out int target)) continue; // untracked id — skip (matches AllOutputsSatisfied)
            if (target == 0) continue; // no target set — treat as neutral
            score *= ((float)GlobalInventory.instance.Quantity(iq.item) / target);
        }
        foreach (ItemQuantity iq in outputs){
            if (!targets.TryGetValue(iq.item.id, out int target)) continue; // untracked id — skip (matches AllOutputsSatisfied)
            if (target == 0) continue; // no target set — treat as neutral
            score /= ((float)GlobalInventory.instance.Quantity(iq.item) / target);
        }
        return score;
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