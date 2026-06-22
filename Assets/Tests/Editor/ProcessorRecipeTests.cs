using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for the unified processor-recipe model: Db.GetProcessorRecipes (the building→
// recipes resolver its Processor scores among) and Recipe.FormatDuration (the seconds-vs-days
// batch-time display). Processor recipes are ordinary Recipes with a `duration` (isProcessorRecipe).
//
// Builds Recipe objects directly via field initializers — no Db load needed. The JSON load path
// (recipesDb → Recipe[OnDeserialized], liang→fen) is exercised transitively by the PlayMode smoke
// tests, where Db.Awake runs and a broken loader trips Db.ValidateProcessorRecipes' LogError.
[TestFixture]
public class ProcessorRecipeTests {

    // Saved in OneTimeSetUp, restored in OneTimeTearDown so we don't leak our test registry into
    // sibling fixtures (Db.processorRecipesByBuilding is a shared static).
    static Dictionary<string, List<Recipe>> savedRegistry;

    [OneTimeSetUp]
    public void OneTimeSetUp(){
        savedRegistry = Db.processorRecipesByBuilding;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown(){
        Db.processorRecipesByBuilding = savedRegistry;
    }

    [Test]
    public void GetProcessorRecipes_KnownBuilding_ReturnsList(){
        var a = new Recipe { id = 0, tile = "brewery", duration = 960 };
        var b = new Recipe { id = 1, tile = "brewery", duration = 480 };
        Db.processorRecipesByBuilding = new Dictionary<string, List<Recipe>> {
            { "brewery", new List<Recipe> { a, b } },
        };
        var list = Db.GetProcessorRecipes("brewery");
        Assert.That(list, Is.Not.Null);
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list[0], Is.SameAs(a));
    }

    [Test]
    public void GetProcessorRecipes_UnknownBuilding_ReturnsNull(){
        Db.processorRecipesByBuilding = new Dictionary<string, List<Recipe>> {
            { "brewery", new List<Recipe> { new Recipe { tile = "brewery", duration = 1 } } },
        };
        Assert.That(Db.GetProcessorRecipes("sawmill"), Is.Null);
    }

    [Test]
    public void GetProcessorRecipes_NullRegistry_ReturnsNull(){
        // The Building constructor calls GetProcessorRecipes; in a fixture where Db was never
        // loaded the registry is null. The null-guard must hold rather than NRE.
        Db.processorRecipesByBuilding = null;
        Assert.That(Db.GetProcessorRecipes("brewery"), Is.Null);
    }

    [Test]
    public void GetProcessorRecipes_EmptyListForBuilding_ReturnsNull(){
        // An empty list is treated as "no recipes" (guards on Count > 0).
        Db.processorRecipesByBuilding = new Dictionary<string, List<Recipe>> {
            { "brewery", new List<Recipe>() },
        };
        Assert.That(Db.GetProcessorRecipes("brewery"), Is.Null);
    }

    [Test]
    public void FormatDuration_ShortBatchesShowSeconds(){
        Assert.That(Recipe.FormatDuration(8f),  Is.EqualTo("8s"));
        Assert.That(Recipe.FormatDuration(59f), Is.EqualTo("59s"));
    }

    [Test]
    public void FormatDuration_LongBatchesShowDays(){
        // 60s threshold → in-game days via World.ticksInDay (480 s/day).
        Assert.That(Recipe.FormatDuration(World.ticksInDay),        Is.EqualTo("1 day"));
        Assert.That(Recipe.FormatDuration(World.ticksInDay * 2f),   Is.EqualTo("2 days"));
        Assert.That(Recipe.FormatDuration(World.ticksInDay * 1.5f), Is.EqualTo("1.5 days"));
    }
}
