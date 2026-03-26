using UnityEditor;

/// <summary>
/// Automatically enables Read/Write on all textures under Resources/Sprites/Buildings/.
/// Required so WaterController.ScanWaterPixels can read pixel data at runtime to detect
/// WaterMarkerColor pixels. Runs automatically whenever a building sprite is imported
/// or reimported — no manual Import Settings changes needed.
/// </summary>
public class BuildingSpritePostprocessor : AssetPostprocessor {
    private const string BuildingsPath = "Assets/Resources/Sprites/Buildings/";

    void OnPreprocessTexture() {
        if (!assetPath.StartsWith(BuildingsPath)) return;
        var importer = assetImporter as TextureImporter;
        if (importer == null || importer.isReadable) return;
        importer.isReadable = true;
    }
}
