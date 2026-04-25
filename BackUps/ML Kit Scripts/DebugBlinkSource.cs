using System;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class DebugBlinkSource : MonoBehaviour, IBlinkSource
{
    public event Action Blinked;

    void Update()
    {
        bool pressed = false;

        // New Input System
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            pressed = true;

        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
            pressed = true;
#endif

        // Old Input Manager (works if you have "Both" enabled)
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0))
            pressed = true;
#endif

        if (pressed)
            Blinked?.Invoke();
    }
}

