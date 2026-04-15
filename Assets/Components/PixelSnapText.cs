using UnityEngine;
using TMPro;

// Snaps TMP text glyph positions to integer pixels per-line.
// Fixes distorted text caused by center/right alignment placing
// the line origin at a fractional pixel. Attach to any TextMeshProUGUI.
[RequireComponent(typeof(TextMeshProUGUI))]
public class PixelSnapText : MonoBehaviour {
    private TextMeshProUGUI tmp;

    void Awake() {
        tmp = GetComponent<TextMeshProUGUI>();
    }

    void LateUpdate() {
        // Force TMP to recalculate its layout so we always snap from clean
        // (unmodified) vertex positions. Without this, we'd be re-snapping
        // already-snapped vertices each frame, causing leftward drift.
        tmp.ForceMeshUpdate();
        var textInfo = tmp.textInfo;
        if (textInfo == null) return;

        for (int i = 0; i < textInfo.lineCount; i++) {
            var line = textInfo.lineInfo[i];
            if (line.characterCount == 0) continue;

            var first = textInfo.characterInfo[line.firstVisibleCharacterIndex];
            if (!first.isVisible) continue;

            float offsetX = first.bottomLeft.x - Mathf.Round(first.bottomLeft.x);
            if (Mathf.Abs(offsetX) < 0.001f) continue;

            for (int c = line.firstCharacterIndex; c <= line.lastCharacterIndex; c++) {
                var ch = textInfo.characterInfo[c];
                if (!ch.isVisible) continue;

                var verts = textInfo.meshInfo[ch.materialReferenceIndex].vertices;
                int v = ch.vertexIndex;
                verts[v].x -= offsetX;
                verts[v + 1].x -= offsetX;
                verts[v + 2].x -= offsetX;
                verts[v + 3].x -= offsetX;
            }
        }

        for (int i = 0; i < textInfo.meshInfo.Length; i++) {
            textInfo.meshInfo[i].mesh.vertices = textInfo.meshInfo[i].vertices;
            tmp.UpdateGeometry(textInfo.meshInfo[i].mesh, i);
        }
    }
}
