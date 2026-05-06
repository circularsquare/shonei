using UnityEngine;

// Drifting parallax clouds parented to the world (not a camera). Each Update
// the layer follows the main camera with a parallax factor and slowly drifts
// horizontally; child clouds wrap-around when they pass the screen edges.
//
// Per-cloud tint sampling: each child SpriteRenderer's color is set to the
// SkyGradient sample at the cloud's screen-Y, so clouds belong to whichever
// horizon→zenith band they're floating in (warm near horizon at sunset, cool
// near zenith). Raw colors only — the lighting pipeline handles ambient × sun
// at composite time. See SPEC-rendering.md §Sky / background.
public class CloudLayer : MonoBehaviour {
    public float parallaxFactorX = 0.5f;
    public float parallaxFactorY = 0.3f;
    public float driftSpeed = 0.2f;      // horizontal drift in world units/sec
    public float wrapWidth = 30f;

    [Tooltip("How strongly clouds adopt the sky's color. 0 = always white, 1 = exact sky color (clouds invisible against sky). 0.4 leaves clouds noticeably brighter than the surrounding sky band while still picking up its hue (e.g. warm at sunset).")]
    [Range(0f, 1f)] public float skyTintStrength = 0.4f;

    private Camera cam;
    private Vector2 lastCamPos;

    void Start() {
        cam = Camera.main;
        lastCamPos = cam.transform.position;
    }

    void Update() {
        Vector2 camPos = cam.transform.position;
        Vector2 delta = camPos - lastCamPos;

        transform.position += new Vector3(
            delta.x * parallaxFactorX + driftSpeed * Time.deltaTime,
            delta.y * parallaxFactorY,
            0
        );
        lastCamPos = camPos;

        // Sample the gradient through the SkyCamera so the viewport-Y matches
        // the gradient's reference frame (SkyCamera frustum, not the main cam).
        Camera tintCam = (SkyCamera.instance != null && SkyCamera.instance.BgCam != null)
            ? SkyCamera.instance.BgCam : cam;

        foreach (Transform cloud in transform) {
            float screenLeft = camPos.x - wrapWidth / 2;
            float screenRight = camPos.x + wrapWidth / 2;
            if (cloud.position.x < screenLeft)
                cloud.position += new Vector3(wrapWidth, 0, 0);
            else if (cloud.position.x > screenRight)
                cloud.position -= new Vector3(wrapWidth, 0, 0);

            SpriteRenderer sr = cloud.GetComponent<SpriteRenderer>();
            if (sr == null) continue;
            Vector3 vp = tintCam.WorldToViewportPoint(cloud.position);
            // Lerp toward white so clouds stay brighter than the sky band
            // they're in (otherwise cloud_color × lightRT == sky × lightRT and
            // the cloud blends invisibly into the sky).
            Color skyTint = SkyGradient.SampleAtViewportY(vp.y);
            sr.color = Color.Lerp(Color.white, skyTint, skyTintStrength);
        }
    }
}
