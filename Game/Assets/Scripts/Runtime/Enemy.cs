using System;
using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    public class Enemy : MonoBehaviour, IDamageable
    {
        [SerializeField] private float speed = 2f;
        [SerializeField] private int maxHealth = 3;
        [SerializeField] private int damageToBase = 1;

        private PathWaypoints path;
        private BaseHealth target;
        private int nextIndex;
        private bool finished;

        public float Speed => speed;
        public int MaxHealth => maxHealth;
        public int CurrentHealth { get; private set; } = 3;
        public int DamageToBase => damageToBase;
        public float DistanceTravelled { get; private set; }
        public bool IsDead => CurrentHealth <= 0;

        /// <summary>Raised once when the enemy is killed by damage.</summary>
        public event Action<Enemy> Died;

        /// <summary>Raised once when the enemy arrives and damages the base.</summary>
        public event Action<Enemy> ReachedBase;

        /// <summary>Places the enemy at waypoint 0 and starts it toward waypoint 1.</summary>
        public void Init(PathWaypoints newPath, BaseHealth newTarget, float newSpeed, int health)
        {
            path = newPath;
            target = newTarget;
            speed = newSpeed;
            maxHealth = health;
            CurrentHealth = health;
            DistanceTravelled = 0f;
            finished = false;
            nextIndex = 1;

            if (path != null && path.Count > 0)
                transform.position = path.GetPoint(0);
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        public void Tick(float dt)
        {
            if (finished || path == null || dt <= 0f) return;

            float remaining = speed * dt;
            while (remaining > 0f && nextIndex < path.Count)
            {
                Vector3 goal = path.GetPoint(nextIndex);
                Vector3 pos = transform.position;
                goal.y = path.GetPoint(nextIndex).y;
                Vector3 delta = goal - pos;
                float dist = delta.magnitude;

                if (dist > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);

                if (dist <= remaining)
                {
                    transform.position = goal;
                    DistanceTravelled += dist;
                    remaining -= dist;
                    nextIndex++;
                }
                else
                {
                    transform.position = pos + delta / dist * remaining;
                    DistanceTravelled += remaining;
                    remaining = 0f;
                }
            }

            if (nextIndex >= path.Count)
                Arrive();
        }

        public void TakeDamage(int amount)
        {
            if (finished || amount <= 0) return;

            CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
            if (CurrentHealth > 0) return;

            finished = true;
            Died?.Invoke(this);
            Remove();
        }

        private void Arrive()
        {
            finished = true;
            if (target != null) target.TakeDamage(damageToBase);
            ReachedBase?.Invoke(this);
            Remove();
        }

        private void Remove()
        {
            if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);
        }
    }
}
