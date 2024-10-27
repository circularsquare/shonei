using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class RecipePanel : MonoBehaviour {
    public static RecipePanel instance {get; protected set;}
    public GameObject recipesGo;
    private World world;

    // this class keeps track of allowed recipes and priorities
        // per mouse? or global

    void Start() {    
        if (instance != null) {
            Debug.LogError("there should only be one recipe panel");}
        instance = this;   


    }
}
