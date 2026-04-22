using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Row prefab for the jobs panel (one per job). Carries the "jobname: count"
// TMP label on the root and three +/-/0 buttons as children. Button clicks
// route to OnClickJobButton; pointer clicks on the label itself bubble to
// this GameObject and go through OnPointerClick — the buttons are Selectables
// and consume their own clicks, so there's no routing conflict.
public class JobDisplay : MonoBehaviour, IPointerClickHandler {
    public void OnClickJobButton(){
        string buttonType = EventSystem.current.currentSelectedGameObject.name.Split('_')[1];
        string jobName = this.gameObject.name.Split('_')[1];
        AnimalController.instance.OnClickJobAssignment(jobName, buttonType);
    }

    // Clicking the label cycles the InfoPanel through mice that have this job.
    // Buttons never reach here — they're UGUI Selectables that handle clicks
    // themselves. The ExecuteHierarchy walk for label clicks starts on this
    // GameObject (TMP is on the root), so this handler fires directly.
    public void OnPointerClick(PointerEventData eventData){
        string jobName = this.gameObject.name.Split('_')[1];
        AnimalController.instance.SelectAnimalWithJob(jobName);
    }
}
