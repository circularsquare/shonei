using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for Db.GetProcessorRecipe — the resolver that links a building to the
// passive-conversion recipe its Processor runs (see ProcessorRecipe in Db.cs).
//
// Scope: the lookup contract only — first-of-list, null for an unknown building, and
// null-safety when the registry itself hasn't been built yet (GetProcessorRecipe is
// called from the Building constructor, which in tests can run before Db is loaded).
//
// ── Deferred to integration tests ────────────────────────────────────
// The full processorRecipesDb.json → ProcessorRecipe load path (JSON [OnDeserialized],
// liang→fen resolution via Db.itemByName) is an integration concern — the same line
// RecipeScoringTests draws for Recipe. It is exercised transitively by the PlayMode
// smoke / snapshot tests: Db.Awake runs there, and a malformed file or broken loader
// trips Db.ValidateProcessorRecipes' Debug.LogError, failing those tests. So here we
// build ProcessorRecipe objects directly via field initializers, no Db load needed.
[TestFixture]
public class ProcessorRecipeTests {

    // Saved in OneTimeSetUp, restored in OneTimeTearDown so we don't leak our test
    // registry into sibling fixtures (Db.processorRecipesByBuilding is a shared static).
    static Dictionary<string, List<ProcessorRecipe>> savedRegistry;

    [OneTimeSetUp]
    public void OneTimeSetUp(){
        savedRegistry = Db.processorRecipesByBuilding;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown(){
        Db.processorRecipesByBuilding = savedRegistry;
    }

    [Test]
    public void GetProcessorRecipe_KnownBuilding_ReturnsFirstRecipe(){
        // Multi-recipe-per-building is a planned extension; today GetProcessorRecipe
        // resolves the first entry. Two recipes on one building documents that contract.
        var first  = new ProcessorRecipe { id = 0, building = "brewery" };
        var second = new ProcessorRecipe { id = 1, building = "brewery" };
        Db.processorRecipesByBuilding = new Dictionary<string, List<ProcessorRecipe>> {
            { "brewery", new List<ProcessorRecipe> { first, second } },
        };
        Assert.That(Db.GetProcessorRecipe("brewery"), Is.SameAs(first));
    }

    [Test]
    public void GetProcessorRecipe_UnknownBuilding_ReturnsNull(){
        Db.processorRecipesByBuilding = new Dictionary<string, List<ProcessorRecipe>> {
            { "brewery", new List<ProcessorRecipe> { new ProcessorRecipe { building = "brewery" } } },
        };
        Assert.That(Db.GetProcessorRecipe("sawmill"), Is.Null);
    }

    [Test]
    public void GetProcessorRecipe_NullRegistry_ReturnsNull(){
        // The Building constructor calls GetProcessorRecipe; in a fixture where Db was
        // never loaded the registry is null. The null-guard must hold rather than NRE.
        Db.processorRecipesByBuilding = null;
        Assert.That(Db.GetProcessorRecipe("brewery"), Is.Null);
    }

    [Test]
    public void GetProcessorRecipe_EmptyListForBuilding_ReturnsNull(){
        // An empty list shouldn't index [0] — GetProcessorRecipe guards on Count > 0.
        Db.processorRecipesByBuilding = new Dictionary<string, List<ProcessorRecipe>> {
            { "brewery", new List<ProcessorRecipe>() },
        };
        Assert.That(Db.GetProcessorRecipe("brewery"), Is.Null);
    }
}
