using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for the foundry melt-pool core (Foundry.StepMelt / StepAlloy). These are pure
// static steppers over plain data (a chunk list + a Dictionary molten pool), so no live Building /
// Inventory / World is needed. The Db foundry-recipe lists are populated with minimal hand-built
// recipes in [SetUp] and restored in [TearDown] so the tests don't depend on recipesDb.json.
[TestFixture]
public class FoundryTests {
    Item malachite, cassiterite, moltenCopper, moltenTin, moltenBronze;
    Recipe meltMalachite, alloyBronze;

    List<Recipe> _origMelt, _origAlloy, _origCast;

    [SetUp]
    public void SetUp() {
        _origMelt  = Db.foundryMeltRecipes;
        _origAlloy = Db.foundryAlloyRecipes;
        _origCast  = Db.foundryCastRecipes;

        // ids mirror itemsDb for readability; the steppers only use reference identity + id.
        malachite    = new Item { id = 34, name = "malachite" };
        cassiterite  = new Item { id = 35, name = "cassiterite" };
        moltenCopper = new Item { id = 46, name = "molten copper", itemClass = ItemClass.Liquid };
        moltenTin    = new Item { id = 47, name = "molten tin",    itemClass = ItemClass.Liquid };
        moltenBronze = new Item { id = 48, name = "molten bronze", itemClass = ItemClass.Liquid };

        // 1 malachite (100 fen) → 1 molten copper (100 fen); ramp 300→600; 48 s/chunk; 80 heat/liang.
        meltMalachite = new Recipe { foundryOp = "melt", meltTempMin = 300, meltTempIdeal = 600, meltDuration = 48, meltHeatCost = 80 };
        meltMalachite.inputs  = new[] { new ItemQuantity(malachite, 100) };
        meltMalachite.outputs = new[] { new ItemQuantity(moltenCopper, 100) };

        // 1 molten copper + 1 molten tin → 2 molten bronze (1:1 input ratio = current economy).
        alloyBronze = new Recipe { foundryOp = "alloy" };
        alloyBronze.inputs  = new[] { new ItemQuantity(moltenCopper, 100), new ItemQuantity(moltenTin, 100) };
        alloyBronze.outputs = new[] { new ItemQuantity(moltenBronze, 200) };

        Db.foundryMeltRecipes  = new List<Recipe> { meltMalachite };
        Db.foundryAlloyRecipes = new List<Recipe> { alloyBronze };
        Db.foundryCastRecipes  = new List<Recipe>();
    }

    [TearDown]
    public void TearDown() {
        Db.foundryMeltRecipes  = _origMelt;
        Db.foundryAlloyRecipes = _origAlloy;
        Db.foundryCastRecipes  = _origCast;
    }

    // ── Melting ───────────────────────────────────────────────────────────────
    [Test]
    public void Melt_FullyMeltsAtIdeal_PoursYieldIntoPool() {
        var chunks = new List<MeltChunk> { new MeltChunk(malachite, 100) };
        var pool = new Dictionary<int, int>();
        float heat = 1000f;
        // At ideal temp (600) rate = 1; meltDuration 48 → dt 48 completes the melt in one step.
        Foundry.StepMelt(chunks, pool, ref heat, 600f, 48f);
        Assert.That(chunks, Is.Empty, "fully-melted chunk is removed");
        Assert.That(pool.GetValueOrDefault(moltenCopper.id), Is.EqualTo(100), "100 fen malachite → 100 fen molten copper");
    }

    [Test]
    public void Melt_TimeIsSizeIndependent() {
        var chunks = new List<MeltChunk> { new MeltChunk(malachite, 100), new MeltChunk(malachite, 1000) };
        var pool = new Dictionary<int, int>();
        float heat = 100000f;
        // Half the melt time at ideal → both chunks reach progress 0.5 regardless of size.
        Foundry.StepMelt(chunks, pool, ref heat, 600f, 24f);
        Assert.That(chunks.Count, Is.EqualTo(2), "neither chunk fully melted yet");
        Assert.That(chunks[0].meltProgress, Is.EqualTo(0.5f).Within(1e-4));
        Assert.That(chunks[1].meltProgress, Is.EqualTo(0.5f).Within(1e-4));
    }

    [Test]
    public void Melt_HeatDrainScalesWithChunkSize() {
        // Fully melt a 1-liang chunk, then a fresh 10-liang chunk; the big one drains 10× the heat.
        float heatSmall = 100000f, heatBig = 100000f;
        var poolS = new Dictionary<int, int>(); var poolB = new Dictionary<int, int>();
        var small = new List<MeltChunk> { new MeltChunk(malachite, 100) };
        var big   = new List<MeltChunk> { new MeltChunk(malachite, 1000) };
        Foundry.StepMelt(small, poolS, ref heatSmall, 600f, 48f);
        Foundry.StepMelt(big,   poolB, ref heatBig,   600f, 48f);
        Assert.That(100000f - heatSmall, Is.EqualTo(80f).Within(1e-2),  "1 liang × 80 heat");
        Assert.That(100000f - heatBig,   Is.EqualTo(800f).Within(1e-2), "10 liang × 80 heat");
    }

    [Test]
    public void Melt_BelowMinTemp_Regresses_NeverBelowZero() {
        var chunks = new List<MeltChunk> { new MeltChunk(malachite, 100) { meltProgress = 0.3f } };
        var pool = new Dictionary<int, int>();
        float heat = 1000f;
        // Ambient 17 < min 300 → negative rate → progress decreases (re-solidifies).
        Foundry.StepMelt(chunks, pool, ref heat, 17f, 10f);
        Assert.That(chunks[0].meltProgress, Is.LessThan(0.3f), "cold pool re-solidifies the chunk");
        Assert.That(chunks[0].meltProgress, Is.GreaterThan(0f));
        // Drive it well past zero — must clamp at 0, never pour into the pool.
        for (int i = 0; i < 20; i++) Foundry.StepMelt(chunks, pool, ref heat, 17f, 48f);
        Assert.That(chunks[0].meltProgress, Is.EqualTo(0f));
        Assert.That(pool, Is.Empty, "a chunk that never reached full progress produces nothing");
    }

    // ── Auto-alloy ──────────────────────────────────────────────────────────────
    [Test]
    public void Alloy_ConvertsByRatio_KeepsLeftover() {
        // 300 copper + 100 tin, 1:1 ratio → 1 unit (100+100 → 200 bronze); 200 copper left over.
        var pool = new Dictionary<int, int> { { moltenCopper.id, 300 }, { moltenTin.id, 100 } };
        Foundry.StepAlloy(pool, _ => true);
        Assert.That(pool.GetValueOrDefault(moltenBronze.id), Is.EqualTo(200));
        Assert.That(pool.GetValueOrDefault(moltenCopper.id), Is.EqualTo(200), "surplus copper stays molten");
        Assert.That(pool.ContainsKey(moltenTin.id), Is.False, "tin fully consumed → entry dropped");
    }

    [Test]
    public void Alloy_TargetGated_DoesNotFire() {
        // Target=copper would disable the bronze alloy: copper + tin must NOT combine.
        var pool = new Dictionary<int, int> { { moltenCopper.id, 100 }, { moltenTin.id, 100 } };
        Foundry.StepAlloy(pool, id => false);
        Assert.That(pool.ContainsKey(moltenBronze.id), Is.False);
        Assert.That(pool.GetValueOrDefault(moltenCopper.id), Is.EqualTo(100));
        Assert.That(pool.GetValueOrDefault(moltenTin.id), Is.EqualTo(100));
    }
}
