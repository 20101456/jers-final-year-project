using System;

public static class EncounterBus
{
    public static Action ContinuePressed;

    public static void RaiseContinue()
    {
        ContinuePressed?.Invoke();
    }
}
