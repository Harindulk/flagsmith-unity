using UnityEditor;
using UnityEngine;

/// <summary>Imports the art under Resources/Art as crisp sprites: 128 pixels per unit, no mipmaps, no lossy compression.</summary>
sealed class ArtImport : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.Contains("/Resources/Art/")) return;
        var t = (TextureImporter)assetImporter;
        t.textureType = TextureImporterType.Sprite;
        t.spriteImportMode = SpriteImportMode.Single;
        t.spritePixelsPerUnit = 128;
        t.mipmapEnabled = false;
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        t.alphaIsTransparency = true;
        t.textureCompression = TextureImporterCompression.Uncompressed; // flat-colour art: lossy formats band, and Brotli shrinks it anyway
    }
}
