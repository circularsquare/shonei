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
    public bool Unreserve(){
        if (reserved <= 0){
            Debug.LogError("unreserved when had 0 reserved!");
            return false;
        }
        reserved--;
        return true;
    }
} 