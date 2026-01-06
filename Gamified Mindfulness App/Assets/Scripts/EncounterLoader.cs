using UnityEngine;
using UnityEngine.SceneManagement;

public class EncounterLoader : MonoBehaviour
{
    [Header("Scene Names (must match Build Settings)")]
    public string encounterSceneName = "EncounterFirefly1";

    [Header("Disable these GameObjects during encounter (Canvas, GridWorld, etc.)")]
    public GameObject[] disableObjects;

    [Header("Disable these Behaviours during encounter (scripts, cameras, etc.)")]
    public Behaviour[] disableBehaviours;

    bool _running;

    public void StartEncounter()
    {
        if (_running) return;
        _running = true;

        // Disable big stuff (Canvas etc.)
        foreach (var go in disableObjects)
            if (go != null) go.SetActive(false);

        // Disable behaviours (movement scripts / cameras)
        foreach (var b in disableBehaviours)
            if (b != null) b.enabled = false;

        EncounterBus.ContinuePressed += EndEncounter;

        SceneManager.LoadSceneAsync(encounterSceneName, LoadSceneMode.Additive);
    }

    public void EndEncounter()
    {
        EncounterBus.ContinuePressed -= EndEncounter;

        SceneManager.UnloadSceneAsync(encounterSceneName);

        foreach (var b in disableBehaviours)
            if (b != null) b.enabled = true;

        foreach (var go in disableObjects)
            if (go != null) go.SetActive(true);

        _running = false;
    }
}

