using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// this is the script for a singular row job display prefab, not the whole job panel
public class JobDisplay : MonoBehaviour {
    public void OnClickJobButton(){
        string buttonType = EventSystem.current.currentSelectedGameObject.name.Split('_')[1];
        string jobName = this.gameObject.name.Split('_')[1];
        AnimalController.instance.OnClickJobAssignment(jobName, buttonType);
    }


}
