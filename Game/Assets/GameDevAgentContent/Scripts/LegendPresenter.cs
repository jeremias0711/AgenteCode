using UnityEngine;

/// <summary>
/// Visual layer of a defender represented by a legend (phase 11). Lives on the tower root next to the gameplay
/// components and only listens to them: it never changes damage, range, rate, health, cost or targeting.
///  - Turns the legend model towards the closest enemy in range (the model only; colliders and fire points keep
///    their own transforms).
///  - Plays Attack when the tower fires (ProjectileTower/BombTower.Fired) or, for the slow tower, at a fixed visual
///    interval while it is slowing enemies.
///  - Plays Hit on TowerHealth.HealthChanged and Death on TowerHealth.Removed (the model is detached so the death
///    animation can finish after the tower object is destroyed; it has no collider and no gameplay component).
///  - Plays Spawn once when invoked, if the legend has that animation.
/// Root motion is always off: the animations never move the defender.
/// </summary>
[DisallowMultipleComponent]
public class LegendPresenter : MonoBehaviour
{
    public LegendId legend;
    [Tooltip("Root of the legend model (child of the tower). Rotated towards the target; detached on death.")]
    public Transform visualRoot;
    public Animator animator;
    [Tooltip("Seconds the death animation stays visible after the tower is removed.")]
    public float deathDuration = 1.6f;
    [Tooltip("Slow tower only: seconds between two Attack animations while enemies are inside the slowing area.")]
    public float slowAttackInterval = 2.5f;
    public float turnSpeed = 6f;

    public const string IdleState = "Idle";
    public const string AttackState = "Attack";
    public const string HitState = "Hit";
    public const string DeathState = "Death";
    public const string SpawnState = "Spawn";

    private static readonly int AttackHash = Animator.StringToHash(AttackState);
    private static readonly int HitHash = Animator.StringToHash(HitState);
    private static readonly int DeathHash = Animator.StringToHash(DeathState);
    private static readonly int SpawnHash = Animator.StringToHash(SpawnState);

    private Tower tower;
    private TowerHealth health;
    private ProjectileTower projectileTower;
    private BombTower bombTower;
    private SlowdownTower slowTower;
    private float slowTimer;
    private float lastHitTime = -10f;
    private bool live;
    private bool dead;

    /// <summary>Number of Attack animations started (read by the automated tests).</summary>
    public int AttackCount { get; private set; }
    public int HitCount { get; private set; }
    public bool DeathPlayed { get; private set; }
    public string LastState { get; private set; }

    void Awake()
    {
        tower = GetComponent<Tower>();
        health = GetComponent<TowerHealth>();
        projectileTower = GetComponent<ProjectileTower>();
        bombTower = GetComponent<BombTower>();
        slowTower = GetComponent<SlowdownTower>();
        if (animator != null)
        {
            animator.applyRootMotion = false;
        }
    }

    void Start()
    {
        // The placement ghost is an instance with the Tower disabled: it shows the idle legend and nothing else.
        live = tower != null && tower.enabled;
        if (!live)
        {
            return;
        }
        if (projectileTower != null) projectileTower.Fired += OnFired;
        if (bombTower != null) bombTower.Fired += OnFired;
        if (health != null)
        {
            health.HealthChanged += OnHealthChanged;
            health.Removed += OnRemoved;
        }
        Play(SpawnHash, SpawnState, 0f);
    }

    void OnDestroy()
    {
        if (projectileTower != null) projectileTower.Fired -= OnFired;
        if (bombTower != null) bombTower.Fired -= OnFired;
        if (health != null)
        {
            health.HealthChanged -= OnHealthChanged;
            health.Removed -= OnRemoved;
        }
    }

    void Update()
    {
        if (!live || dead || tower == null)
        {
            return;
        }

        Transform target = ClosestEnemy();
        if (target != null && visualRoot != null)
        {
            Vector3 to = target.position - visualRoot.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.0001f)
            {
                Quaternion want = Quaternion.LookRotation(to);
                visualRoot.rotation = Quaternion.Slerp(visualRoot.rotation, want, turnSpeed * Time.deltaTime);
            }
        }

        if (slowTower != null)
        {
            slowTimer -= Time.deltaTime;
            if (target != null && slowTimer <= 0f)
            {
                slowTimer = slowAttackInterval;
                OnFired();
            }
        }
    }

    private Transform ClosestEnemy()
    {
        Transform best = null;
        float bestDistance = float.MaxValue;
        foreach (EnemyController enemy in tower.enemiesInRange)
        {
            if (enemy == null)
            {
                continue;
            }
            float d = (enemy.transform.position - transform.position).sqrMagnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = enemy.transform;
            }
        }
        return best;
    }

    private void OnFired()
    {
        if (dead)
        {
            return;
        }
        AttackCount++;
        Play(AttackHash, AttackState, 0.08f);
    }

    private void OnHealthChanged(TowerHealth h)
    {
        if (dead || h.IsDestroyed || h.currentHealth <= 0f || Time.time - lastHitTime < 0.6f)
        {
            return;
        }
        lastHitTime = Time.time;
        HitCount++;
        Play(HitHash, HitState, 0.05f);
    }

    private void OnRemoved(TowerHealth h)
    {
        if (dead)
        {
            return;
        }
        dead = true;
        if (visualRoot == null || !Application.isPlaying)
        {
            return;
        }
        bool hasDeath = animator != null && animator.HasState(0, DeathHash);
        if (!hasDeath)
        {
            return; // destroyed together with the tower, as before
        }
        visualRoot.SetParent(null, true);
        DeathPlayed = true;
        Play(DeathHash, DeathState, 0.05f);
        Destroy(visualRoot.gameObject, deathDuration);
    }

    private void Play(int hash, string stateName, float fade)
    {
        if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null
            || !animator.HasState(0, hash))
        {
            return;
        }
        LastState = stateName;
        if (fade <= 0f)
        {
            animator.Play(hash, 0, 0f);
        }
        else
        {
            animator.CrossFadeInFixedTime(hash, fade, 0, 0f);
        }
    }
}
