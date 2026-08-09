using System;
using System.Collections.Generic;
using UnityEngine;

namespace GardenMR
{
    // Menu data source. Deliberately holds only a scene name string + a small thumbnail per
    // entry — NEVER a GaussianSplatAsset or a reference to an environment scene's content.
    // GardenMR_Shell (and therefore this catalog) is present in every environment scene, so
    // any direct asset reference here would pull every environment's splat data (~10-20 MB
    // each) into memory on every scene load. The scene name is enough: SceneTransition loads
    // it through the Build Settings scene list, which is Unity's own on-demand asset boundary.
    [CreateAssetMenu(fileName = "EnvironmentCatalog", menuName = "GardenMR/Environment Catalog")]
    public class EnvironmentCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Exact scene name as it appears in Build Settings (not the full asset path).")]
            public string m_SceneName;
            [Tooltip("Small thumbnail shown on the menu card. Loaded with the catalog, not the environment.")]
            public Texture2D m_Thumbnail;
            [Tooltip("Display label. No text rendering in the menu yet (no CJK font in the project), " +
                     "kept for tooltips/authoring/debug logging.")]
            public string m_DisplayId;
        }

        public List<Entry> m_Environments = new();
    }
}
