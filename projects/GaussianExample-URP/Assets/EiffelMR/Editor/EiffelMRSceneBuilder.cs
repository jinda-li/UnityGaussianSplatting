using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using GardenMR;
using GaussianSplatting.Runtime;

namespace EiffelMR.EditorTools
{
    // Builds MR_Eiffel from scratch, because building it by hand is a dozen
    // objects with two dozen cross-references and getting one of them wrong
    // produces a scene that looks right and does nothing.
    //
    // The rig, passthrough and dive controller all come from the existing
    // 3DGS_MR_Shell prefab rather than being recreated here - that prefab is the
    // thing the other MR scenes are built on, and a second, subtly different
    // copy of it is how the two would drift apart.
    //
    // Everything this adds is under one root, so re-running the item replaces
    // the play group and leaves the rig alone.
    public static class EiffelMRSceneBuilder
    {
        const string k_ScenePath = "Assets/EiffelMR/MR_Eiffel.unity";
        const string k_ShellPath = "Assets/GardenMR/Prefabs/3DGS_MR_Shell.prefab";
        const string k_ParticleShader = "Gaussian Splatting/Tower Particles";
        const string k_MaterialDir = "Assets/EiffelMR/Materials";
        const string k_RootName = "EiffelMR Play";

        [MenuItem("Tools/Eiffel MR/Build MR_Eiffel Scene")]
        public static void BuildScene()
        {
            // Re-running this on the scene it produced is the normal case while
            // iterating, and prompting for it stalls the Editor's main thread on
            // a modal dialog - which is invisible and indistinguishable from a
            // hang when the build is being driven from the CLI. Save that one
            // silently; anything else still asks.
            var open = EditorSceneManager.GetActiveScene();
            if (open.isDirty && open.path == k_ScenePath)
                EditorSceneManager.SaveScene(open);
            else if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                                    NewSceneMode.Single);

            var shell = AssetDatabase.LoadAssetAtPath<GameObject>(k_ShellPath);
            if (shell)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(shell, scene);
                instance.transform.position = Vector3.zero;
            }
            else
            {
                Debug.LogWarning($"[EiffelMR] {k_ShellPath} not found. The scene will " +
                                 "have the play group but no rig - add one before testing.");
            }

            BuildPlayGroup();

            Directory.CreateDirectory(Path.GetDirectoryName(k_ScenePath));
            EditorSceneManager.SaveScene(scene, k_ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[EiffelMR] Built {k_ScenePath}.");
        }

        [MenuItem("Tools/Eiffel MR/Rebuild Play Group In Open Scene")]
        public static void BuildPlayGroup()
        {
            var existing = GameObject.Find(k_RootName);
            if (existing)
                Object.DestroyImmediate(existing);

            Directory.CreateDirectory(k_MaterialDir);

            var root = new GameObject(k_RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Eiffel MR play group");

            var playGroup = new GameObject("PlayGroup");
            playGroup.transform.SetParent(root.transform, false);

            var dive = Object.FindFirstObjectByType<TabletopDiveController>();

            // The miniature is the splat rig, not a model of it.
            //
            // TabletopDiveController already holds the splat at table scale with
            // a cutout hiding everything but the tower, and Dive() grows that
            // same object to 1:1. So the thing the player picks up out of the
            // bubble has to BE that rig - anything else means the tower they
            // throw and the tower they arrive at are two different objects, and
            // the swap is visible.
            //
            // The placeholder is only built when there is no splat to use, so
            // the scene is still testable before the asset is trained.
            TuneForTower(dive);
            var rig = dive != null ? dive.m_Rig : null;
            var bubble = BuildBubble(playGroup.transform, rig, out var tower);
            var ring = BuildRing(playGroup.transform);

            // Runtime placement is EiffelBubbleSession's job - it puts these in
            // front of wherever the player is actually looking. These are only
            // so that opening the scene in the editor shows the layout instead
            // of everything piled on the origin.
            bubble.transform.localPosition = new Vector3(0f, 1.25f, 0.55f);
            ring.transform.localPosition = new Vector3(0f, 0.005f, 2.0f);
            var reveal = BuildSkyShell(root.transform);

            var session = root.AddComponent<EiffelBubbleSession>();
            session.m_PlayGroup = playGroup;
            session.m_Bubble = bubble;
            session.m_Tower = tower;
            session.m_Ring = ring;
            session.m_Reveal = reveal;
            session.m_Dive = dive;

            bubble.m_Tower = tower.GetComponent<Rigidbody>();
            tower.m_Ring = ring;
            tower.m_Reveal = reveal;
            tower.m_Dive = dive;
            if (dive)
            {
                tower.m_HandleRig = dive.m_HandleRig;
                tower.m_SplatRoot = dive.m_SplatRenderer ? dive.m_SplatRenderer.transform : null;
                tower.m_LandingSpawn = dive.m_DefaultSpawnPoint;
                tower.m_Particles = SetUpParticles(dive);
            }

            if (!dive)
            {
                Debug.LogWarning("[EiffelMR] No TabletopDiveController in the scene, so the " +
                                 "miniature is the box placeholder and the throw grows only " +
                                 "the sky. Add the dive rig and re-run to get the real thing.");
            }
            else if (!dive.m_SplatRenderer)
            {
                Debug.LogWarning("[EiffelMR] The dive controller has no splat renderer yet. " +
                                 "The rig is wired up, but until an Eiffel splat asset is " +
                                 "assigned there is nothing in the bubble to look at.");
            }
            if (dive && !dive.m_DefaultSpawnPoint)
            {
                Debug.LogWarning("[EiffelMR] No spawn point: the landing will grow the world " +
                                 "but not decide where the player stands. Author one at the " +
                                 "hero viewpoint, splat-local (34, -116, 1.65).");
            }

            Selection.activeGameObject = root;
        }

        // The miniature in the bubble is drawn as a drifting cloud of the
        // splat's own points, which is a render shader on the splat renderer
        // rather than anything added next to it. A gaussian splat is already a
        // particle cloud, so this shows the real data as the samples it is made
        // of - the points that drift in the bubble are the points the player
        // ends up standing in.
        static TowerParticles SetUpParticles(TabletopDiveController dive)
        {
            var renderer = dive ? dive.m_SplatRenderer : null;
            if (!renderer)
                return null;

            var shader = Shader.Find(k_ParticleShader);
            if (!shader)
            {
                Debug.LogWarning($"[EiffelMR] Shader '{k_ParticleShader}' not found, so the " +
                                 "miniature will draw as an ordinary splat. Check that " +
                                 "TowerParticles.shader compiled.");
                return null;
            }

            // The renderer builds its own Material from this field at runtime,
            // which is why the parameters are globals and there is no material
            // asset to edit.
            renderer.m_ShaderSplats = shader;
            EditorUtility.SetDirty(renderer);

            var particles = renderer.GetComponent<TowerParticles>();
            if (!particles)
                particles = renderer.gameObject.AddComponent<TowerParticles>();
            particles.m_Renderer = renderer;
            // Starts dispersed: the bubble is the first thing the player sees.
            particles.m_Solidify = 0f;
            return particles;
        }

        // The shell prefab is authored for the Garden, whose subject is a few
        // metres across. The Eiffel Tower is 324 m, which puts every scale
        // constant in the dive controller two to three orders of magnitude out:
        // at the Garden's table scale the "miniature" is a twelve-metre tower
        // standing in the player's room.
        static void TuneForTower(TabletopDiveController dive)
        {
            if (!dive)
                return;

            // 0.0009 x 324 m = 29 cm: something you can hold.
            dive.m_DefaultTableScale = 0.0009f;
            // The tabletop dive grows the world about fifty times. This one
            // grows it more than a thousand, and packing that into the same
            // second reads as a lurch rather than as arriving somewhere.
            dive.m_DiveDuration = 2.2f;

            if (dive.m_ScaleHandle)
            {
                // Room to size it in the hand, and no room to accidentally
                // scale it back to a Garden-sized object: 32 cm to 81 cm.
                //
                // The floor is 0.001 rather than the 0.0005 the plan asked for
                // because SplatScaleHandle declares [Min(0.001f)]. Writing a
                // smaller value here would work until someone touched the field
                // in the inspector, at which point it would silently snap back -
                // a setting that changes when looked at is worse than a slightly
                // coarser one.
                dive.m_ScaleHandle.m_MinScale = 0.001f;
                dive.m_ScaleHandle.m_MaxScale = 0.0025f;
            }
            EditorUtility.SetDirty(dive);
        }

        static TowerBubble BuildBubble(Transform parent, Transform rig, out ThrownTower tower)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "TowerBubble";
            go.transform.SetParent(parent, false);
            // 32 cm across. The shell has to contain the model with room to
            // spare, and it has to be big enough to aim a finger at: at 24 cm
            // the placeholder's mast came out through the top.
            go.transform.localScale = Vector3.one * 0.32f;

            var shell = go.GetComponent<MeshRenderer>();
            shell.sharedMaterial = LoadOrCreate("BubbleGlass", "EiffelMR/BubbleGlass");
            shell.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // The sphere's own collider becomes the poke volume; it must not
            // block the hand or the finger stops at the shell instead of
            // reaching the depth the pop test wants.
            var collider = go.GetComponent<SphereCollider>();
            collider.isTrigger = true;

            var bubble = go.AddComponent<TowerBubble>();

            if (rig)
            {
                // Wrap the existing rig instead of building anything: the shell
                // follows it, and the component that throws it goes on the rig.
                bubble.m_FollowTower = true;
                tower = rig.GetComponent<ThrownTower>();
                if (!tower)
                    tower = rig.gameObject.AddComponent<ThrownTower>();
                // Sized to the miniature it has to contain, with room to aim a
                // finger at. A 324 m tower at the controller's table scale is
                // about 29 cm, so the shell is 40.
                go.transform.localScale = Vector3.one * 0.40f;
                bubble.m_CentreOffset = 0.15f;
            }
            else
            {
                bubble.m_FollowTower = false;
                tower = BuildMiniature(go.transform);
            }
            return bubble;
        }

        static ThrownTower BuildMiniature(Transform parent)
        {
            var go = new GameObject("MiniTower");
            go.transform.SetParent(parent, false);
            // Parented to the shell, so undo the parent's scale: the model is
            // authored in metres and should be about 20 cm tall in the hand.
            go.transform.localScale = Vector3.one / 0.32f * 0.20f;

            // Placeholder geometry. Four tapering boxes and a mast read as the
            // tower's silhouette from across a room, which is all this has to do
            // until the trained asset exists - and it makes the scene testable
            // now rather than after a fifty-minute render and a training run.
            var stand = new GameObject("PlaceholderModel");
            stand.transform.SetParent(go.transform, false);
            // The model is authored standing on its own origin, so centre it in
            // the shell rather than hanging it from the middle - otherwise the
            // whole top half of the tower is outside the bubble.
            stand.transform.localPosition = new Vector3(0f, -0.5f, 0f);
            AddBox(stand.transform, new Vector3(0f, 0.10f, 0f), new Vector3(0.42f, 0.20f, 0.42f));
            AddBox(stand.transform, new Vector3(0f, 0.34f, 0f), new Vector3(0.26f, 0.30f, 0.26f));
            AddBox(stand.transform, new Vector3(0f, 0.66f, 0f), new Vector3(0.12f, 0.36f, 0.12f));
            AddBox(stand.transform, new Vector3(0f, 0.92f, 0f), new Vector3(0.05f, 0.20f, 0.05f));

            var body = go.AddComponent<Rigidbody>();
            body.mass = 0.4f;
            body.isKinematic = true;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            var collider = go.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 0.5f, 0f);
            collider.size = new Vector3(0.42f, 1.0f, 0.42f);

            var grab = go.AddComponent<XRGrabInteractable>();
            grab.useDynamicAttach = true;
            grab.throwOnDetach = false;   // ThrownTower does its own release maths

            return go.AddComponent<ThrownTower>();
        }

        static void AddBox(Transform parent, Vector3 centre, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Part";
            box.transform.SetParent(parent, false);
            box.transform.localPosition = centre;
            box.transform.localScale = size;
            Object.DestroyImmediate(box.GetComponent<BoxCollider>());
            box.GetComponent<MeshRenderer>().sharedMaterial =
                LoadOrCreate("TowerPlaceholder", "Universal Render Pipeline/Lit",
                             new Color(0.22f, 0.15f, 0.09f));
        }

        static LandingRing BuildRing(Transform parent)
        {
            var go = new GameObject("LandingRing");
            go.transform.SetParent(parent, false);

            var disc = GameObject.CreatePrimitive(PrimitiveType.Quad);
            disc.name = "RingQuad";
            disc.transform.SetParent(go.transform, false);
            disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            disc.transform.localScale = Vector3.one * 1.3f;
            Object.DestroyImmediate(disc.GetComponent<MeshCollider>());

            var renderer = disc.GetComponent<MeshRenderer>();
            // A ring, not a square. The first build used an unlit quad, which
            // renders as exactly that: a blue card on the floor.
            renderer.sharedMaterial = LoadOrCreate("LandingRing", "EiffelMR/LandingRingGlow");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var ring = go.AddComponent<LandingRing>();
            ring.m_Renderer = renderer;
            ring.m_Radius = 0.55f;
            return ring;
        }

        static HexSkyReveal BuildSkyShell(Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "HexSkyShell";
            go.transform.SetParent(parent, false);
            // Big enough to be outside anything the player can walk to, small
            // enough to stay inside the camera's far plane.
            go.transform.localScale = Vector3.one * 400f;
            Object.DestroyImmediate(go.GetComponent<SphereCollider>());

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = LoadOrCreate("HexSkyReveal", "EiffelMR/HexSkyReveal");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return go.AddComponent<HexSkyReveal>();
        }

        static Material LoadOrCreate(string name, string shaderName, Color? colour = null)
        {
            string path = $"{k_MaterialDir}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing)
                return existing;

            var shader = Shader.Find(shaderName);
            if (!shader)
            {
                Debug.LogWarning($"[EiffelMR] Shader '{shaderName}' not found; using Lit.");
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }

            var material = new Material(shader) { name = name };
            if (colour.HasValue)
            {
                material.SetColor("_BaseColor", colour.Value);
                if (colour.Value.a < 1f)
                {
                    // URP needs the surface type flipped explicitly; setting only
                    // the alpha leaves the material opaque and the ring renders
                    // as a solid white disc.
                    material.SetFloat("_Surface", 1f);
                    material.SetFloat("_Blend", 0f);
                    material.renderQueue = 3000;
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 0f);
                }
            }
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
