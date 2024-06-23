using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class JobDisplay : MonoBehaviour
{

    public void OnClickJobButton(){
        string buttonType = EventSystem.current.currentSelectedGameObject.name.Split('_')[1];
        string jobName = this.gameObject.name.Split('_')[1];
        AnimalController.instance.OnClickJobAssignment(jobName, buttonType);
    }

}
