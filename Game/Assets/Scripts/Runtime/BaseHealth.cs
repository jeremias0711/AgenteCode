using System;
using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    /// <summary>Health of the player's book (the base enemies try to reach).</summary>
    public class BaseHealth : MonoBehaviour, IDamageable
    {
        [SerializeField] private int maxHealth = 10;

        private int currentHealth;
        private bool initialized;
        private bool destroyedRaised;

        public int MaxHealth => maxHealth;

        public int CurrentHealth
        {
            get
            {
                EnsureInitialized();
                return currentHealth;
            }
        }

        public bool IsDead => CurrentHealth <= 0;

        /// <summary>Raised on every change with (current, max).</summary>
        public event Action<int, int> HealthChanged;

        /// <summary>Raised once when health reaches 0.</summary>
        public event Action Destroyed;

        private void Awake()
        {
            EnsureInitialized();
        }

        public void Configure(int newMaxHealth)
        {
            maxHealth = Mathf.Max(1, newMaxHealth);
            ResetHealth();
        }

        public void ResetHealth()
        {
            initialized = true;
            destroyedRaised = false;
            currentHealth = maxHealth;
            HealthChanged?.Invoke(currentHealth, maxHealth);
        }

        public void TakeDamage(int amount)
        {
            if (amount <= 0) return;
            EnsureInitialized();
            if (currentHealth <= 0) return;

            currentHealth = Mathf.Max(0, currentHealth - amount);
            HealthChanged?.Invoke(currentHealth, maxHealth);

            if (currentHealth == 0 && !destroyedRaised)
            {
                destroyedRaised = true;
                Destroyed?.Invoke();
            }
        }

        private void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;
            currentHealth = maxHealth;
        }
    }
}
