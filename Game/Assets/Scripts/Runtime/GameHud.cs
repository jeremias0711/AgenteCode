using UnityEngine;

namespace GameDevAgent.TowerDefense
{
    /// <summary>IMGUI heads-up display: base health, wave progress and end-of-game panel.</summary>
    public class GameHud : MonoBehaviour
    {
        [SerializeField] private GameManager game;
        [SerializeField] private BaseHealth baseHealth;
        [SerializeField] private WaveSpawner spawner;

        public void Configure(GameManager newGame, BaseHealth newBase, WaveSpawner newSpawner)
        {
            game = newGame;
            baseHealth = newBase;
            spawner = newSpawner;
        }

        private void OnGUI()
        {
            DrawStatusBox();
            DrawEndPanel();
        }

        private void DrawStatusBox()
        {
            GUI.Box(new Rect(10, 10, 220, 90), GUIContent.none);

            if (baseHealth != null)
            {
                int max = Mathf.Max(1, baseHealth.MaxHealth);
                int cur = baseHealth.CurrentHealth;
                GUI.Label(new Rect(20, 14, 200, 22), $"Libro: {cur}/{max}");

                Rect bar = new Rect(20, 38, 200, 14);
                Color previous = GUI.color;
                GUI.color = Color.black;
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
                GUI.color = Color.Lerp(Color.red, Color.green, (float)cur / max);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01((float)cur / max), bar.height),
                    Texture2D.whiteTexture);
                GUI.color = previous;
            }

            if (spawner != null)
            {
                GUI.Label(new Rect(20, 56, 200, 22), $"Oleada {spawner.CurrentWave}/{spawner.TotalWaves}");
                GUI.Label(new Rect(20, 76, 200, 22), $"Enemigos vivos: {spawner.AliveEnemies}");
            }
        }

        private void DrawEndPanel()
        {
            if (game == null || game.State == GameState.Playing) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 64,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            style.normal.textColor = game.State == GameState.Won ? Color.green : Color.red;

            string text = game.State == GameState.Won ? "VICTORIA" : "DERROTA";
            float cx = Screen.width / 2f;
            float cy = Screen.height / 2f;
            GUI.Label(new Rect(cx - 300, cy - 90, 600, 100), text, style);

            if (GUI.Button(new Rect(cx - 80, cy + 30, 160, 40), "Reiniciar"))
                game.Restart();
        }
    }
}
