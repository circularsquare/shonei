using TMPro;
using UnityEngine;

// Stamps the build version onto a TMP label. Drop on any TextMeshProUGUI (e.g. the
// menu corner) so playtesters can tell you which build they're on in a bug report.
//
// Single source of truth is PlayerSettings bundleVersion, read here via
// Application.version — the same value Tools/build-and-publish.ps1 passes to itch
// as --userversion, so the in-game number and the itch build version never drift.
[RequireComponent(typeof(TextMeshProUGUI))]
public class VersionLabel : MonoBehaviour {
    [SerializeField] string prefix = "v";   // shown before the number, e.g. "v0.1.0"

    void Awake() {
        GetComponent<TextMeshProUGUI>().text = prefix + Application.version;
    }
}
