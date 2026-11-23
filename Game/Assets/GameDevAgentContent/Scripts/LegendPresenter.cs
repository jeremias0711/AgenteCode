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
    [Tooltip("Normalized time of the attack impact in the Attack clip (< 0 = off: the clip plays from its start). When set, " +
             "each shot starts the clip just before the impact and speeds up the rest so it ends before the next shot. " +
             "Presentation only: the shot itself is still fired by the tower.")]
    public float attackImpact = -1f;
    [Tooltip("Seconds between the shot and the visual impact when attackImpact is set.")]
    public float attackLead = 0.1f;

    public const string IdleState = "Idle";
    public const string AttackState = "Attack";
    public const string HitState = "Hit";
    public const string DeathState = "Death";
    public const string SpawnState = "Spawn";
    /// <summary>Float parameter driving the speed of the Attack state (only in controllers with attackImpact).</summary>
    public const string AttackSpeedParameter = "AttackSpeed";
    private const float MaxAttackSpeed = 2.5f;
    private static readonly int AttackSpeedHash = Animator.StringToHash(AttackSpeedParameter);

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
    /// <summary>Speed and start (normalized) of the last synchronised Attack (read by the automated tests).</summary>
    public float LastAttackSpeed { get; private set; } = 1f;
    public float LastAttackStart { get; private set; }

    private float attackClipLength;
    private bool attackSpeedParam;

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

    private bool attackInfoRead;

    private void ReadAttackInfo()
    {
        if (attackInfoRead || animator == null || animator.runtimeAnimatorController == null) return;
        attackInfoRead = true;
        foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
            if (clip != null && clip.name == AttackState) attackClipLength = clip.length;
        foreach (AnimatorControllerParameter p in animator.parameters)
            if (p.nameHash == AttackSpeedHash && p.type == AnimatorControllerParameterType.Float) attackSpeedParam = true;
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
        if (attackImpact >= 0f) ReadAttackInfo();
        if (attackImpact >= 0f && attackClipLength > 0f && attackSpeedParam && tower != null && tower.fireRate > 0f)
        {
            PlaySynchronisedAttack();
            return;
        }
        Play(AttackHash, AttackState, 0.08f);
    }

    /// <summary>
    /// The shot is already fired (the tower is the source of truth). The clip starts attackLead seconds before its
    /// impact, and its speed makes the remaining part end within 90% of the current fire interval (upgrades included),
    /// so consecutive shots never cut the strike. Never slower than the authored speed.
    /// </summary>
    private void PlaySynchronisedAttack()
    {
        float window = tower.fireRate * 0.9f - attackLead;
        float tail = (1f - attackImpact) * attackClipLength; // clip seconds from the impact to the end
        float speed = window > 0.05f ? Mathf.Clamp(tail / window, 1f, MaxAttackSpeed) : MaxAttackSpeed;
        float startSeconds = Mathf.Max(0f, attackImpact * attackClipLength - attackLead * speed);
        LastAttackSpeed = speed;
        LastAttackStart = startSeconds / attackClipLength;
        if (animator == null || !animator.isActiveAndEnabled || !animator.HasState(0, AttackHash)) return;
        animator.SetFloat(AttackSpeedHash, speed);
        LastState = AttackState;
        animator.CrossFadeInFixedTime(AttackHash, 0.06f, 0, startSeconds);
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
