using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GardenMR
{
    // Cross-scene singleton that owns environment switching. LoadSceneAsync(Single) tears
    // down and rebuilds the XR Origin along with everything else, which would be a jarring
    // pose/tracking jump if shown directly — so this fades the outgoing scene's vignette
    // down to the same closed aperture Dive uses (revealing passthrough, not black, since
    // MR passthrough is already on in Place mode), loads the new scene, then fades back out.
    //
    // Must be the only thing in the environment scenes marked DontDestroyOnLoad; everything
    // else (GardenMR_Shell, the splat content) is expected to die with LoadSceneMode.Single.
    public class SceneTransition : MonoBehaviour
    {
        public static SceneTransition Instance { get; private set; }

        [Tooltip("Seconds to close/open the vignette on either side of the scene load.")]
        public float m_FadeDuration = 0.35f;

        [Tooltip("Extra frames to wait after the new scene reports loaded, so its Awake/Start " +
                 "(SplatGlobalsReset, TabletopDiveController.Start, first PushGlobals) finish " +
                 "and the first frame renders before the vignette opens.")]
        public int m_SettleFrames = 2;

        public bool IsBusy { get; private set; }

        void Awake()
        {
            if (Instance && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void LoadEnvironment(string sceneName)
        {
            if (IsBusy || string.IsNullOrEmpty(sceneName))
                return;
            StartCoroutine(Routine(sceneName));
        }

        IEnumerator Routine(string sceneName)
        {
            IsBusy = true;

            var outgoing = FindFirstObjectByType<TabletopDiveController>();
            if (outgoing)
            {
                outgoing.BeginExclusiveTransition();
                yield return FadeVignette(outgoing, outgoing.ApertureOpen, outgoing.ApertureClosedMin);
            }

            var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            while (op != null && !op.isDone)
                yield return null;

            for (int i = 0; i < m_SettleFrames; i++)
                yield return null;

            var incoming = FindFirstObjectByType<TabletopDiveController>();
            if (incoming)
            {
                incoming.BeginExclusiveTransition();
                incoming.SetVignette(incoming.ApertureClosedMin);
                yield return FadeVignette(incoming, incoming.ApertureClosedMin, incoming.ApertureOpen);
                incoming.EndExclusiveTransition();
            }

            IsBusy = false;
        }

        static IEnumerator FadeVignette(TabletopDiveController controller, float from, float to)
        {
            float t = 0f;
            float duration = Instance ? Instance.m_FadeDuration : 0.35f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, duration);
                controller.SetVignette(Mathf.Lerp(from, to, Mathf.Clamp01(t)));
                yield return null;
            }
            controller.SetVignette(to);
        }
    }
}
