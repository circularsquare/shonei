using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;


public class CloudLayer : MonoBehaviour {
    public float parallaxFactorX = 0.5f;
    public float parallaxFactorY = 0.3f;
    public float driftSpeed = 0.2f;      // horizontal drift in world units/sec
    public float wrapWidth = 30f;
    private Camera cam;
    private Vector2 lastCamPos;

    void Start() {
        cam = Camera.main;
        lastCamPos = cam.transform.position;
    }

    void Update() {
        Vector2 camPos = cam.transform.position;
        Vector2 delta = camPos - lastCamPos;

        transform.position += new Vector3(
            delta.x * parallaxFactorX + driftSpeed * Time.deltaTime,
            delta.y * parallaxFactorY,
            0
        );
        lastCamPos = camPos;

        foreach (Transform cloud in transform) {
            float screenLeft = camPos.x - wrapWidth / 2;
            float screenRight = camPos.x + wrapWidth / 2;
            if (cloud.position.x < screenLeft)
                cloud.position += new Vector3(wrapWidth, 0, 0);
            else if (cloud.position.x > screenRight)
                cloud.position -= new Vector3(wrapWidth, 0, 0);
        }
    }
}