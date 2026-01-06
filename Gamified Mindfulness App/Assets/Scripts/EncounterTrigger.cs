using UnityEngine;

public class EncounterTrigger : MonoBehaviour
{
    public Transform player;
    public EncounterLoader loader;
    public float triggerRadius = 0.3f;
    public bool oneShot = true;

    bool _fired;

    void Update()
    {
        if (_fired && oneShot) return;
        if (player == null || loader == null) return;

        float d = Vector2.Distance(transform.position, player.position);
        if (d <= triggerRadius)
        {
            _fired = true;
            loader.StartEncounter();
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, triggerRadius);
    }
}

