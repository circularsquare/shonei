using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Reservable {
    public int   capacity;          // hard max (from structType.capacity / JSON)
    public int   effectiveCapacity; // player-set limit; defaults to capacity on construction
    public int   reserved;
    public float reservedAt;        // World.instance.timer value when last Reserve() was called
    public string reservedBy;       // animal name that made the reservation (for logging)
    // Owning task when Reserve was called with a Task overload. Null for string-only callers
    // (WOM orders, home assignments). ExpireIfStale uses this to avoid false-positive expiry
    // on legitimately long tasks — mirrors ItemStack.ExpireIfStale's TaskStillActive guard.
    public Task reservedByTask;

    public Reservable (int capacity){
        this.capacity = capacity;
        this.effectiveCapacity = capacity;
        reserved = 0;
    }
    // Gates on effectiveCapacity so the player can restrict workers below the hard max.
    public bool Available() => reserved < effectiveCapacity;

    // String-only overload — for callers without a Task context (home assignments,
    // WOM orders). No TaskStillActive guard; these paths rely purely on time-based expiry
    // (or don't go through ExpireIfStale at all, in the case of WOM which uses PruneStale).
    public bool Reserve(string by = null){
        if (!Available()) return false;
        reserved++;
        reservedAt = World.instance.timer;
        reservedBy = by;
        reservedByTask = null;
        return true;
    }

    // Task-based overload — use this whenever a Task owns the reservation. Enables the
    // TaskStillActive check in ExpireIfStale so a reservation isn't cleared while its
    // owning task is still the animal's active task.
    public bool Reserve(Task by){
        if (!Available()) return false;
        reserved++;
        reservedAt = World.instance.timer;
        reservedByTask = by;
        reservedBy = by?.animal?.aName;
        return true;
    }

    public bool Unreserve(int n = 1, string label = ""){
        if (reserved < n){
            string ctx = label.Length > 0 ? $" [{label}]" : "";
            Debug.LogError($"Unreserve underflow{ctx}: reserved={reserved} n={n}");
            return false;
        }
        reserved -= n;
        if (reserved == 0) { reservedByTask = null; reservedBy = null; }
        return true;
    }

    // Safety net: clears reserved if held longer than maxAge AND the owning task (if known)
    // is no longer the animal's active task. When reservedByTask is null (string-only callers)
    // the guard falls through, preserving the original time-only behaviour for those paths.
    // Returns true if expired.
    public bool ExpireIfStale(float maxAge, string label = "") {
        if (reserved > 0
            && World.instance.timer - reservedAt > maxAge
            && !TaskStillActive(reservedByTask)) {
            string ctx = label.Length > 0 ? $" [{label}]" : "";
            string by = reservedBy != null ? $" by {reservedBy}" : "";
            Debug.LogWarning($"Cleared stale reservation{ctx}{by}: reserved={reserved} held={World.instance.timer - reservedAt:F0}s");
            reserved = 0;
            reservedByTask = null;
            reservedBy = null;
            return true;
        }
        return false;
    }

    private static bool TaskStillActive(Task t) => t != null && t.animal.task == t;
}
