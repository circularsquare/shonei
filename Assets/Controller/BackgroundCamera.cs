using UnityEngine;
public class BackgroundCamera : MonoBehaviour {
    [Tooltip("How much of the main camera's zoom applies to the background. 0 = never zoom, 1 = zoom equally.")]
    [Range(0f, 1f)] public float zoomFactor = 0.25f;

    Camera bgCam;
    Camera mainCam;
    float baseOrthoSize;

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

        bgCam.backgroundColor = SunController.skyColor * SunController.GetAmbientColor();
    }
}