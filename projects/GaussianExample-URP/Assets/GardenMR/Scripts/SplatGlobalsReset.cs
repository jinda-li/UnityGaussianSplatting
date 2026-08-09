using UnityEngine;
using GardenSplat;
using StylizedSplats;

namespace GardenMR
{
    // Shader.SetGlobal* is process-global and LoadSceneAsync(Single) does not reset it, so
    // a scene switch from an environment with a render-driver component (e.g. the botanical
    // garden's SplatMaterializeController) to one without would otherwise inherit stale
    // dormant-point / stylize parameters. Lives on GardenMR_Shell, which every environment
    // scene has exactly one instance of, so every scene starts from a clean shader-global
    // state before its own render driver (if any) pushes its own values.
    public class SplatGlobalsReset : MonoBehaviour
    {
        void Awake()
        {
            SplatMaterializeController.ResetGlobals();
            StylizedSplatsController.ResetGlobals();
        }
    }
}
