using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Player : MonoBehaviour
{
    // Rigidbody2D rigidbody;
    public float Speed = 3.0f;

    void Start()
    {
        //rigidbody = GetComponent<Rigidbody2D>();
    }


    void FixedUpdate()
    {
        Vector3 pInput = new Vector3(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"), 0);
        GetComponent<Rigidbody2D>().MovePosition(transform.position + pInput * Time.deltaTime * Speed);
    }
}
