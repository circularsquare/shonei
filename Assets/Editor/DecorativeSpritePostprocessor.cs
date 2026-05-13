using UnityEditor;

// Forces Read/Write on every texture under Resources/Sprites/Plants/Decorative/.
// FlowerController.AutoGenerateHeadMask reads main-sprite pixels at runtime to
// derive a stem/head mask from colour (green = stem, anything else = head).
// Without isReadable the GetPixels32 call throws — same pattern as
// BuildingSpritePostprocessor enables it for the water-marker scan on
// Resources/Sprites/Buildings/.
//
// Only `_n.png` (normal map) companions are excluded because they're already
// processed by SpriteNormalMapGenerator's own postprocessor and don't need
// to be readable (their pixels are consumed by the bake pipeline, not at
// runtime). All other PNGs in this folder become readable.
public class DecorativeSpritePostprocessor : AssetPostprocessor {
    private const string DecorativePath = "Assets/Resources/Sprites/Plants/Decorative/";

    void OnPreprocessTexture() {
        if (!assetPath.StartsWith(DecorativePath)) return;
        if (assetPath.EndsWith("_n.png")) return; // normal map companions — handled elsewhere
        var importer = assetImporter as TextureImporter;
        if (importer == null || importer.isReadable) return;
        importer.isReadable = true;
    }
}
