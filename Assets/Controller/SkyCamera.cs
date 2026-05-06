using UnityEngine;

// Stack-Base camera that fills the area "behind" the world — sky, distant
// clouds, stars, and (eventually) parallax background art. Follows the main
// camera with dampened zoom so the sky doesn't snap to the player's zoom level.
//
// Background pixels — anywhere the gradient quad doesn't cover — fall back to
// `backgroundColor`. This is set to the raw zenith `SunController.skyColor`;
// the LightFeature pipeline applies ambient × sun via the composite multiply
// for Sky-layer sprites, so we deliberately do NOT pre-multiply ambient here.
public class SkyCamera : MonoBehaviour {
    public static SkyCamera instance { get; private set; }

    [Tooltip("How much of the main camera's zoom applies to the background. 0 = never zoom, 1 = zoom equally.")]
    [Range(0f, 1f)] public float zoomFactor = 0.25f;

    Camera bgCam;
    Camera mainCam;
    float baseOrthoSize;

    public Camera BgCam => bgCam;

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
    }

    void Start() {
        bgCam   = GetComponent<Camera>();
        mainCam = Camera.main;
        baseOrthoSize = mainCam.orthographicSize;
    }

    void LateUpdate() {
        Vector3 pos = mainCam.transform.position;
        pos.z = transform.position.z;
        transform.position = pos;

        // Dampen zoom: only apply zoomFactor of the main camera's zoom change.
        float mainZoom = mainCam.orthographicSize / baseOrthoSize; // <1 when zoomed in
        float bgZoom = 1f + (mainZoom - 1f) * zoomFactor;
        bgCam.orthographicSize = baseOrthoSize * bgZoom;

        // Raw zenith — fallback only. SkyGradient covers the frustum, and the
        // lighting pipeline multiplies ambient + sun at composite time.
        bgCam.backgroundColor = SunController.skyColor;
    }
}
