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
    public float reservedAt;  // Time.time when last Reserve() was called
    public string reservedBy; // animal name that made the reservation (for logging)

    public Reservable (int capacity){
        this.capacity = capacity;
        this.effectiveCapacity = capacity;
        reserved = 0;
    }
    // Gates on effectiveCapacity so the player can restrict workers below the hard max.
    public bool Available() => reserved < effectiveCapacity;
    public bool Reserve(string by = null){
        if (!Available()) return false;
        reserved++;
        reservedAt = Time.time;
        reservedBy = by;
        return true;
    }
    public bool Unreserve(int n = 1, string label = ""){
        if (reserved < n){
            string ctx = label.Length > 0 ? $" [{label}]" : "";
            Debug.LogError($"Unreserve underflow{ctx}: reserved={reserved} n={n}");
            return false;
        }
        reserved -= n;
        return true;
    }
    // Clears reserved if it has been held longer than maxAge seconds. Returns true if expired.
    public bool ExpireIfStale(float maxAge, string label = "") {
        if (reserved > 0 && Time.time - reservedAt > maxAge) {
            string ctx = label.Length > 0 ? $" [{label}]" : "";
            string by = reservedBy != null ? $" by {reservedBy}" : "";
            Debug.LogWarning($"Cleared stale reservation{ctx}{by}: reserved={reserved} held={Time.time - reservedAt:F0}s");
            reserved = 0;
            reservedBy = null;
            return true;
        }
        return false;
    }
} 