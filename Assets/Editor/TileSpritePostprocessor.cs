using UnityEditor;

// Automatically enables Read/Write on tile textures so TileSpriteCache can
// read pixels at runtime to bake 20×20 border variants.
// Covers both the base tile sprites (Sprites/Tiles/) and the 32×32 border
// atlases (Sprites/Tiles/Sheets/).
public class TileSpritePostprocessor : AssetPostprocessor {
    private const string TilesPath  = "Assets/Resources/Sprites/Tiles/";

    void OnPreprocessTexture() {
        if (!assetPath.StartsWith(TilesPath)) return;
        var importer = assetImporter as TextureImporter;
        if (importer == null || importer.isReadable) return;
        importer.isReadable = true;
    }
}
