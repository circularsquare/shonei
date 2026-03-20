using UnityEngine;
public class BackgroundCamera : MonoBehaviour {
    [Tooltip("Base sky color at full ambient (midday). Tinted darker at night by the ambient light.")]
    public Color baseSkyColor = new Color(0.75f, 0.95f, 0.95f);

    Camera bgCam;
    Camera mainCam;

    void Start() {
        bgCam   = GetComponent<Camera>();
        mainCam = Camera.main;
    }

    void LateUpdate() {
        Vector3 pos = mainCam.transform.position;
        pos.z = transform.position.z;
        transform.position = pos;

        // Tint sky by ambient so it darkens at night / warms at sunrise.
        Color ambient = SunController.GetAmbientColor();
        bgCam.backgroundColor = baseSkyColor * ambient;
    }
}