using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Reservable {
    public int   capacity;
    public int   reserved;
    public float reservedAt; // Time.time when last Reserve() was called

    public Reservable (int capacity){
        this.capacity = capacity;
        reserved = 0;
    }
    public bool Available() => reserved < capacity;
    public bool Reserve(){
        if (!Available()) return false;
        reserved++;
        reservedAt = Time.time;
        return true;
    }
    // Reserves up to n, clamped to what's available. Returns amount actually reserved.
    public int Reserve(int n){
        int amount = Math.Min(n, capacity - reserved);
        if (amount <= 0) return 0;
        reserved += amount;
        reservedAt = Time.time;
        return amount;
    }
    public bool Unreserve(int n = 1){
        if (reserved < n){
            Debug.LogError("unreserved when had less reserved!");
            return false;
        }
        reserved -= n;
        return true;
    }
    // Clears reserved if it has been held longer than maxAge seconds. Returns true if expired.
    public bool ExpireIfStale(float maxAge) {
        if (reserved > 0 && Time.time - reservedAt > maxAge) {
            Debug.LogWarning($"Cleared stale reservation (held {Time.time - reservedAt:F0}s)");
            reserved = 0;
            return true;
        }
        return false;
    }
} 