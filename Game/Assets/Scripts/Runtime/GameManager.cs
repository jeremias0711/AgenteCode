using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDevAgent.TowerDefense
{
    public enum GameState { Playing, Won, Lost }

    public class GameManager : MonoBehaviour
    {
        [SerializeField] private BaseHealth baseHealth;
        [SerializeField] private WaveSpawner spawner;

        public GameState State { get; private set; } = GameState.Playing;

        public event Action<GameState> StateChanged;

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        public void Configure(BaseHealth newBase, WaveSpawner newSpawner)
        {
            Unsubscribe();
            baseHealth = newBase;
            spawner = newSpawner;
            State = GameState.Playing;
            Subscribe();
        }

        public void Restart()
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        private void Subscribe()
        {
            if (baseHealth != null)
            {
                baseHealth.Destroyed -= OnBaseDestroyed;
                baseHealth.Destroyed += OnBaseDestroyed;
            }
            if (spawner != null)
            {
                spawner.AllWavesCompleted -= OnAllWavesCompleted;
                spawner.AllWavesCompleted += OnAllWavesCompleted;
            }
        }

        private void Unsubscribe()
        {
            if (baseHealth != null) baseHealth.Destroyed -= OnBaseDestroyed;
            if (spawner != null) spawner.AllWavesCompleted -= OnAllWavesCompleted;
        }

        private void OnBaseDestroyed() => SetState(GameState.Lost);

        private void OnAllWavesCompleted() => SetState(GameState.Won);

        private void SetState(GameState newState)
        {
            if (State != GameState.Playing) return;
            State = newState;
            StateChanged?.Invoke(State);
        }
    }
}
