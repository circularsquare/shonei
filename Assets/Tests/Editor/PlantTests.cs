using NUnit.Framework;

// EditMode tests for the PlantType growth contracts added with the data-driven fruit-tree
// rework. These are pure property logic — no Plant instance (constructing one needs the
// Unity lifecycle: PlantController, GameObjects). Harvest rewind, RemovalYield scaling, and
// table-driven rendering are behavioural and covered by manual / PlayMode verification.
//
// The load-bearing contract here is maxStage: when a plant ships a growthFrames table it must
// run the full table length, NOT the 4-stages-per-tile formula — Grow/Mature/IsDoneGrowing all
// key off maxStage, so a regression silently breaks every fruit tree's growth.
[TestFixture]
public class PlantTests {

    // A 2-tile apple-style table has 10 stages → maxStage 9, regardless of maxHeight.
    [Test]
    public void MaxStage_UsesGrowthTableLengthWhenPresent() {
        var pt = new PlantType {
            maxHeight = 2,
            growthFrames = new[] {
                new[]{"b0"}, new[]{"b1"}, new[]{"b2"}, new[]{"b3"},
                new[]{"b4","g0"}, new[]{"b4","g1"}, new[]{"b4","g2"}, new[]{"b4","g3"},
                new[]{"b5","g5"}, new[]{"b6","g6"},
            },
        };
        Assert.IsTrue(pt.hasGrowthTable);
        Assert.AreEqual(9, pt.maxStage);
    }

    // Without a table, maxStage falls back to the 4-stages-per-tile formula (4*maxHeight - 1).
    [Test]
    public void MaxStage_FallsBackToHeightFormulaWithoutTable() {
        Assert.IsFalse(new PlantType { maxHeight = 1 }.hasGrowthTable);
        Assert.AreEqual(3, new PlantType { maxHeight = 1 }.maxStage);
        Assert.AreEqual(7, new PlantType { maxHeight = 2 }.maxStage);
        Assert.AreEqual(11, new PlantType { maxHeight = 3 }.maxStage);
    }

    // Fruit-tree harvest behaviour is gated purely on fruitCycleStages > 0.
    [Test]
    public void IsFruitTree_GatedByFruitCycleStages() {
        Assert.IsFalse(new PlantType().isFruitTree);
        Assert.IsTrue(new PlantType { fruitCycleStages = 2 }.isFruitTree);
    }
}
