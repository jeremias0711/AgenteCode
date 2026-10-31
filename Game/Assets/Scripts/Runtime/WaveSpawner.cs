using System;
using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    public class WaveSpawner : MonoBehaviour
    {
        [SerializeField] private Enemy enemyPrefab;
        [SerializeField] private PathWaypoints path;
        [SerializeField] private BaseHealth target;
        [SerializeField] private WaveDefinition[] waves = new WaveDefinition[0];
        [SerializeField] private float initialDelay = 3f;
        [SerializeField] private float timeBetweenWaves = 5f;

        private float timer;
        private int spawnedInWave;
        private bool waveActive;
        private bool completedRaised;
        private bool configured;

        public int CurrentWave { get; private set; }
        public int TotalWaves => waves != null ? waves.Length : 0;
        public int AliveEnemies { get; private set; }
        public bool AllWavesSpawned { get; private set; }

        public event Action<int> WaveStarted;
        public event Action AllWavesCompleted;

        public void Configure(Enemy newEnemyPrefab, PathWaypoints newPath, BaseHealth newTarget,
            WaveDefinition[] newWaves, float newInitialDelay, float newTimeBetweenWaves)
        {
            enemyPrefab = newEnemyPrefab;
            path = newPath;
            target = newTarget;
            waves = newWaves ?? new WaveDefinition[0];
            initialDelay = newInitialDelay;
            timeBetweenWaves = newTimeBetweenWaves;
            ResetState();
        }

        private void ResetState()
        {
            configured = true;
            CurrentWave = 0;
            AliveEnemies = 0;
            AllWavesSpawned = false;
            completedRaised = false;
            waveActive = false;
            spawnedInWave = 0;
            timer = initialDelay;
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        public void Tick(float dt)
        {
            if (!configured) ResetState();
            if (completedRaised || dt <= 0f) return;
            if (target != null && target.IsDead) return;

            if (!AllWavesSpawned)
            {
                timer -= dt;
                // Loop so a large dt can spawn several enemies/waves in one tick.
                while (!AllWavesSpawned && timer <= 0f && !(target != null && target.IsDead))
                {
                    if (!waveActive) StartNextWave();
                    else SpawnEnemy();
                }
            }

            CheckCompleted();
        }

        private void StartNextWave()
        {
            if (CurrentWave >= TotalWaves)
            {
                AllWavesSpawned = true;
                return;
            }

            CurrentWave++;
            waveActive = true;
            spawnedInWave = 0;
            WaveStarted?.Invoke(CurrentWave);
            // First enemy spawns right away: timer stays <= 0.
        }

        private void SpawnEnemy()
        {
            WaveDefinition wave = waves[CurrentWave - 1];

            if (spawnedInWave >= wave.enemyCount)
            {
                FinishWave();
                return;
            }

            if (enemyPrefab != null)
            {
                Enemy enemy = Instantiate(enemyPrefab);
                enemy.Died += OnEnemyGone;
                enemy.ReachedBase += OnEnemyGone;
                AliveEnemies++;
                enemy.Init(path, target, wave.enemySpeed, wave.enemyHealth);
            }
            spawnedInWave++;

            if (spawnedInWave >= wave.enemyCount) FinishWave();
            else timer += Mathf.Max(0.01f, wave.spawnInterval);
        }

        private void FinishWave()
        {
            waveActive = false;
            if (CurrentWave >= TotalWaves) AllWavesSpawned = true;
            else timer += Mathf.Max(0f, timeBetweenWaves);
        }

        private void OnEnemyGone(Enemy enemy)
        {
            enemy.Died -= OnEnemyGone;
            enemy.ReachedBase -= OnEnemyGone;
            AliveEnemies = Mathf.Max(0, AliveEnemies - 1);
            CheckCompleted();
        }

        private void CheckCompleted()
        {
            if (completedRaised || !AllWavesSpawned || AliveEnemies > 0) return;
            if (target != null && target.IsDead) return;

            completedRaised = true;
            AllWavesCompleted?.Invoke();
        }
    }
}
