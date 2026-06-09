using UnityEngine;

// DontDestroyOnLoad host for SaveSync's background upload pump. SaveSync is a
// static helper, but a coroutine needs a live MonoBehaviour and the pump must
// survive the Menu<->Main scene loads (a final save fires exactly as the player
// returns to the menu, and SaveSystem — a Main-scene-only object — is destroyed
// at that moment). Created lazily by SaveSync.QueueUpload via Ensure().
public class SaveSyncRunner : MonoBehaviour {
    static SaveSyncRunner instance;

    // Spawn the runner (once) and start the pump. Idempotent.
    public static void Ensure() {
        if (instance != null) return;
        var go = new GameObject("SaveSyncRunner");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<SaveSyncRunner>();
    }

    // Host a fire-and-forget coroutine on the persistent runner. Used for delete /
    // rename propagation that must outlive the UI row that triggered it (rows are
    // destroyed on list rebuild, which would kill a coroutine started on them).
    public static void Run(System.Collections.IEnumerator co) {
        Ensure();
        instance.StartCoroutine(co);
    }

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        StartCoroutine(SaveSync.UploadPump());
    }
}
