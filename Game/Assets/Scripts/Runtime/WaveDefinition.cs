using System;

namespace GameDevAgent.TowerDefense
{
    [Serializable]
    public class WaveDefinition
    {
        public int enemyCount = 5;
        public float spawnInterval = 1f;
        public float enemySpeed = 2f;
        public int enemyHealth = 3;
    }
}
