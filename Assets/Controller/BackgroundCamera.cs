using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
public class BackgroundCamera : MonoBehaviour {
    private Camera mainCam;
    
    void Start() {
        mainCam = Camera.main;
    }
    
    void LateUpdate() {
        Vector3 pos = mainCam.transform.position;
        pos.z = transform.position.z; // keep its own z
        transform.position = pos;
    }
}