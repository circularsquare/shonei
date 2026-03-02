using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

public class Reservable {
    public int capacity;
    public int reserved;
    
    public Reservable (int capacity){
        this.capacity = capacity;
        reserved = 0;
    }
    public bool Available() => reserved < capacity;
    public bool Reserve(){
        if (!Available()) return false;
        reserved++;
        return true;
    }
    // Reserves up to n, clamped to what's available. Returns amount actually reserved.
    public int Reserve(int n){
        int amount = Math.Min(n, capacity - reserved);
        if (amount <= 0) return 0;
        reserved += amount;
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
} 