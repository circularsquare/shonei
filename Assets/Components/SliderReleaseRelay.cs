using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Fires once when the user finishes interacting with a Slider — on pointer-up,
// covering both drag-release and a single click on the track. Use instead of
// Slider.onValueChanged when applying the value mid-drag is expensive or unsafe.
//
// Motivating case: the UI-scale slider. It lives inside the very Canvas it
// rescales, so applying scale on every onValueChanged would shove the handle
// out from under the cursor mid-drag. Subscribing to OnRelease defers the apply
// to release, while the Slider still animates its handle normally during the drag.
//
// Slider itself doesn't implement IPointerUpHandler, so the EventSystem routes
// pointer-up to this component on the same GameObject.
[RequireComponent(typeof(Slider))]
public class SliderReleaseRelay : MonoBehaviour, IPointerUpHandler {
    public event Action<float> OnRelease;

    Slider _slider;

    void Awake() {
        _slider = GetComponent<Slider>();
    }

    public void OnPointerUp(PointerEventData eventData) {
        if (_slider != null) OnRelease?.Invoke(_slider.value);
    }
}
