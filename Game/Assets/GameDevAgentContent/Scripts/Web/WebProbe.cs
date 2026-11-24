using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Browser verification hook for the Web release (distribution phase). Does nothing unless the game runs as WebGL
/// AND the page URL contains "gdaProbe=" (never the case for players):
///  - gdaProbe=persist : increments a PlayerPrefs counter and saves it (PlayerPrefs is the persistence used by the
///    campaign progress, the collection and the audio volumes). Reloading the page must show the next number.
///  - gdaProbe=scenes  : loads every scene of the build in turn and reports it, then returns to the first scene
///    (checks that the IL2CPP release build, with code stripping, can open every screen and level).
/// Results are written as "[GDA] ..." console lines, read by the page test hook (?gdaReport=1).
/// </summary>
public class WebProbe : MonoBehaviour
{
    private const string Key = "gda_web_probe_runs";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        if (Application.platform != RuntimePlatform.WebGLPlayer) return;
        string url = Application.absoluteURL ?? string.Empty;
        if (url.IndexOf("gdaProbe=", System.StringComparison.Ordinal) < 0) return;
        var go = new GameObject("__GDA_WebProbe");
        DontDestroyOnLoad(go);
        go.AddComponent<WebProbe>().mode = url.Contains("gdaProbe=scenes") ? "scenes" : "persist";
    }

    private string mode;

    private IEnumerator Start()
    {
        yield return null;
        int runs = PlayerPrefs.GetInt(Key, 0) + 1;
        PlayerPrefs.SetInt(Key, runs);
        PlayerPrefs.Save();
        Debug.Log("[GDA] probe persist runs=" + runs + " levelProgressKeys=" + (PlayerPrefs.HasKey("gda_level_completed_1") ? "yes" : "no"));
        if (mode != "scenes") yield break;
        int count = SceneManager.sceneCountInBuildSettings;
        for (int i = 0; i < count; i++)
        {
            string path = SceneUtility.GetScenePathByBuildIndex(i);
            AsyncOperation op = SceneManager.LoadSceneAsync(i);
            float t = 0f;
            while (op != null && !op.isDone && t < 30f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return new WaitForSecondsRealtime(1.5f);
            Debug.Log("[GDA] probe scene " + i + " " + path + " loaded=" + (SceneManager.GetActiveScene().buildIndex == i)
                + " roots=" + SceneManager.GetActiveScene().rootCount + " fps=" + (1f / Mathf.Max(0.0001f, Time.smoothDeltaTime)).ToString("0"));
        }
        SceneManager.LoadScene(0);
        yield return new WaitForSecondsRealtime(1f);
        Debug.Log("[GDA] probe scenes done count=" + count);
    }
}
