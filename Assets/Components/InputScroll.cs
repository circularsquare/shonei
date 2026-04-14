using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

// Generic mouse-wheel → event forwarder. Attach to any UI GameObject; wire
// `onScrollUp` / `onScrollDown` to whatever should fire per notch.
//
// One notch = one event invocation; we don't scale by scrollDelta magnitude
// because that makes stepping discrete values (e.g. integer targets) predictable.
//
// Why separate up/down events instead of a single UnityEvent<int>: parameter-less
// UnityEvents can bind to dynamic methods in the Unity inspector without needing
// a static argument, which sidesteps the "dynamic vs. static" method-picker gotcha.
public class InputScroll : MonoBehaviour, IScrollHandler {
    public UnityEvent onScrollUp;
    public UnityEvent onScrollDown;

    public void OnScroll(PointerEventData e) {
        if (Mathf.Abs(e.scrollDelta.y) < 0.01f) return;
        if (e.scrollDelta.y > 0) onScrollUp?.Invoke();
        else                     onScrollDown?.Invoke();
    }
}
