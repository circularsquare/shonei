using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


public class MenuPanel : MonoBehaviour
{
    public static MenuPanel instance;
    public GameObject[] panels = null;
    public GameObject activePanel;

    public void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one menu panel");}
        instance = this;       
    }
    public void Update(){
        if (panels.Length == 0){
            // LoadPanels(); // deactivaed for now
        }
    }
    public void LoadPanels(){     
        GameObject parent = transform.parent.gameObject;
        if (parent.transform.Find("InventoryPanel") != null){
            panels = new GameObject[] {
                parent.transform.Find("InventoryPanel").gameObject,
                parent.transform.Find("BuildPanel").gameObject,
                parent.transform.Find("JobsPanel").gameObject
            };

            // foreach (GameObject panel in panels){ // disabled for now because it's nice to have all the menus at once!
            //     panel.SetActive(false);
            // }
            // SetActivePanel(panels[0]);
        }
    }
    public void OnClickInventory(){
        SetActivePanel(panels[0]);
    }
    public void OnClickBuild(){
        SetActivePanel(panels[1]); 
    }
    public void OnClickJobs(){
        SetActivePanel(panels[2]);
    }
    public void SetActivePanel(GameObject panel, bool toggle = true){
        if (MouseController.instance != null){ 
            MouseController.instance.SetModeSelect(); }
        if (panel == activePanel && toggle){              // if click same thing twice, close window
            // activePanel.SetActive(false);
            activePanel = null;
            return;
        }
        if (activePanel != null){
            // activePanel.SetActive(false); 
        }
        activePanel = panel;
        activePanel.SetActive(true);
    }
}
